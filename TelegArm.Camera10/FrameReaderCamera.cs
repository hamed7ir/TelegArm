using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace TelegArm.Camera10
{
    public enum CameraPixelFormat { Nv12 = 0, Yuy2 = 1, Bgra = 2 }

    /// <summary>One camera frame. The plane pointers are valid ONLY during the FrameArrived call: the WinRT buffer they
    /// point into is released when it returns (the handoff's lifetime rule, section 5.3).</summary>
    public struct CameraFrame
    {
        public CameraPixelFormat Format;
        public int Width, Height;
        public IntPtr Plane0, Plane1;
        public int Stride0, Stride1;
        public long Ticks;   // SystemRelativeTime in 100 ns ticks, -1 when the frame has none
    }

    public delegate void CameraFrameHandler(ref CameraFrame frame);

    /// <summary>
    /// The Windows 10 camera path (Venetium camera handoff, 2026-10-16; measured there: 30 fps on the Surface, build 15035):
    /// MediaCapture initialised without MemoryPreference, the VideoPreview colour source at the format nearest 640x480@30,
    /// a MediaFrameReader asking for NV12 (the native subtype as the fallback), and one pump thread that takes the latest
    /// frame, copies a GPU surface to the CPU when it has to, and hands the raw planes to <see cref="FrameArrived"/>.
    /// Recording is MediaCapture's own (StartRecordToStreamAsync, H.264 + AAC MP4) with a VideoTransformEffect cropping the
    /// record stream to a centred square, so a round video is no longer the 4:3 picture squeezed into a square.
    /// </summary>
    public sealed class FrameReaderCamera : IDisposable
    {
        private MediaCapture _mc;
        private MediaFrameSource _source;
        private MediaFrameReader _reader;
        private Thread _pump;
        private readonly AutoResetEvent _arrived = new AutoResetEvent(false);
        private volatile bool _stop;
        private volatile int _frames;
        private TimeSpan? _lastTime;
        private InMemoryRandomAccessStream _recStream;
        private bool _previewPausedForRecording;

        public string DeviceId { get; private set; }
        public bool HasAudio { get; private set; }
        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }
        public string Subtype { get; private set; }
        public double Fps { get; private set; }
        public int FramesDelivered { get { return _frames; } }

        /// <summary>On the pump thread. Convert (or copy) what you need before returning.</summary>
        public event CameraFrameHandler FrameArrived;
        public event Action<Exception> Failed;

        private readonly Action<string> _log;
        private void Log(string s) { try { if (_log != null) _log("[CAM10] " + s); } catch { } }

        private FrameReaderCamera(Action<string> log) { _log = log; }

        /// <summary>Opens the camera and starts the frame reader; null when this camera can't stream frames (no usable
        /// source, or no frame within 4 s of starting: the handoff's "this reader does not work" rule). Runs on the thread
        /// pool (MTA), whatever thread calls it.</summary>
        public static Task<FrameReaderCamera> OpenAsync(string deviceId, bool audio, int width, int height, int fps, Action<string> log)
        {
            return Task.Run(async () =>
            {
                var cam = new FrameReaderCamera(log) { DeviceId = deviceId };
                try
                {
                    if (await cam.InitAsync(audio).ConfigureAwait(false) && await cam.StartReaderAsync(width, height, fps).ConfigureAwait(false))
                        return cam;
                }
                catch (Exception ex) { cam.Log("open failed: " + Describe(ex)); }
                cam.Dispose();
                return null;
            });
        }

        private async Task<bool> InitAsync(bool audio)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = DeviceId,
                StreamingCaptureMode = audio ? StreamingCaptureMode.AudioAndVideo : StreamingCaptureMode.Video,
                PhotoCaptureSource = PhotoCaptureSource.Auto,
                // MemoryPreference deliberately UNSET: forcing Cpu made the hardware MFT refuse to start (handoff section 4)
            };
            _mc = new MediaCapture();
            try { await _mc.InitializeAsync(settings).AsTask().ConfigureAwait(false); HasAudio = audio; }
            catch (Exception ex) when (audio && !(ex is UnauthorizedAccessException))
            {
                Log("audio+video init failed (" + Describe(ex) + ") -> video only");
                try { _mc.Dispose(); } catch { }
                _mc = new MediaCapture();
                settings.StreamingCaptureMode = StreamingCaptureMode.Video;
                await _mc.InitializeAsync(settings).AsTask().ConfigureAwait(false);
                HasAudio = false;
            }
            _mc.Failed += (s, e) =>
            {
                Log("MediaCapture.Failed 0x" + e.Code.ToString("X8") + " " + e.Message);
                var h = Failed; if (h != null) h(new Exception("Camera error 0x" + e.Code.ToString("X8") + ": " + e.Message));
            };
            Log("init " + sw.ElapsedMilliseconds + " ms, audio=" + HasAudio);
            return true;
        }

        /// <summary>The colour source with this stream type, if any of its formats is usable from the CPU.</summary>
        private MediaFrameSource FindSource(MediaStreamType type)
        {
            foreach (var fs in _mc.FrameSources.Values)
            {
                if (fs.Info.SourceKind != MediaFrameSourceKind.Color || fs.Info.MediaStreamType != type) continue;
                if (fs.SupportedFormats.Any(Usable)) return fs;
                Log(type + " source has no NV12/YUY2 format (" + string.Join(",", fs.SupportedFormats.Select(f => f.Subtype).Distinct()) + ")");
            }
            return null;
        }

        private static bool Usable(MediaFrameFormat f)
        {
            string st = f.Subtype ?? "";
            return (st.Equals("NV12", StringComparison.OrdinalIgnoreCase) || st.Equals("YUY2", StringComparison.OrdinalIgnoreCase))
                   && f.VideoFormat != null && f.VideoFormat.Width % 2 == 0 && f.VideoFormat.Height % 2 == 0 && f.VideoFormat.Width > 0;
        }

        private static double FpsOf(MediaFrameFormat f)
        {
            return f.FrameRate != null && f.FrameRate.Denominator > 0 ? (double)f.FrameRate.Numerator / f.FrameRate.Denominator : 0;
        }

        private async Task<bool> StartReaderAsync(int tw, int th, int tfps)
        {
            // Pick by stream type, never "the first Color source" (on the Surface VideoRecord is listed first and is opaque).
            _source = FindSource(MediaStreamType.VideoPreview) ?? FindSource(MediaStreamType.VideoRecord);
            if (_source == null) { Log("no usable colour frame source"); return false; }

            // The format: nearest the target, preferring not larger and not slower (handoff section 3.4); try the best three.
            var ranked = _source.SupportedFormats.Where(Usable)
                .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - tw) + Math.Abs((int)f.VideoFormat.Height - th)
                              + (f.VideoFormat.Width > tw || f.VideoFormat.Height > th ? 100000 : 0)
                              + (FpsOf(f) + 0.5 < tfps ? 50000 : 0))
                .Take(3).ToList();
            bool set = false;
            foreach (var f in ranked)
            {
                try
                {
                    await _source.SetFormatAsync(f).AsTask().ConfigureAwait(false);
                    FrameWidth = (int)f.VideoFormat.Width; FrameHeight = (int)f.VideoFormat.Height; Fps = FpsOf(f);
                    set = true;
                    break;
                }
                catch (Exception ex) { Log("SetFormat " + f.VideoFormat.Width + "x" + f.VideoFormat.Height + " " + f.Subtype + " failed: " + Describe(ex)); }
            }
            if (!set)
            {
                var cur = _source.CurrentFormat;   // keep what the camera is on
                if (cur == null || cur.VideoFormat == null) return false;
                FrameWidth = (int)cur.VideoFormat.Width; FrameHeight = (int)cur.VideoFormat.Height; Fps = FpsOf(cur);
            }

            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "camera-pump" };
            _pump.Start();

            // NV12 first (the 14.5 ms copy on the Tegra); if it won't start or stays silent, the native subtype.
            foreach (string subtype in new[] { MediaEncodingSubtypes.Nv12, null })
            {
                try
                {
                    _reader = subtype != null
                        ? await _mc.CreateFrameReaderAsync(_source, subtype).AsTask().ConfigureAwait(false)
                        : await _mc.CreateFrameReaderAsync(_source).AsTask().ConfigureAwait(false);
                    _reader.FrameArrived += OnFrameArrived;
                    var st = await _reader.StartAsync().AsTask().ConfigureAwait(false);
                    if (st != MediaFrameReaderStartStatus.Success) throw new Exception("StartAsync: " + st);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (_frames == 0 && sw.ElapsedMilliseconds < 4000) await Task.Delay(50).ConfigureAwait(false);
                    if (_frames > 0)
                    {
                        Subtype = subtype ?? (_source.CurrentFormat != null ? _source.CurrentFormat.Subtype : "native");
                        Log("streaming " + FrameWidth + "x" + FrameHeight + " " + Subtype + " @" + Fps.ToString("0.#") + " fps, first frame " + sw.ElapsedMilliseconds + " ms");
                        return true;
                    }
                    Log("reader (" + (subtype ?? "native") + ") gave no frame in 4 s");
                }
                catch (Exception ex) { Log("reader (" + (subtype ?? "native") + ") failed: " + Describe(ex)); }
                await StopReaderAsync().ConfigureAwait(false);
            }
            return false;
        }

        private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args) { _arrived.Set(); }   // the pump does the work

        private void PumpLoop()
        {
            while (!_stop)
            {
                _arrived.WaitOne(100);
                var reader = _reader;
                if (_stop || reader == null) continue;
                try { Pump(reader); }
                catch (Exception ex) { Log("pump: " + Describe(ex)); Thread.Sleep(50); }
            }
        }

        private void Pump(MediaFrameReader reader)
        {
            using (var frame = reader.TryAcquireLatestFrame())
            {
                if (frame == null) return;
                var t = frame.SystemRelativeTime;
                if (t.HasValue && _lastTime.HasValue && t.Value == _lastTime.Value) return;   // the same frame again
                _lastTime = t;
                var vmf = frame.VideoMediaFrame;
                if (vmf == null) return;
                SoftwareBitmap sb = vmf.SoftwareBitmap;
                bool owned = false;
                if (sb == null && vmf.Direct3DSurface != null)
                {
                    var copy = SoftwareBitmap.CreateCopyFromSurfaceAsync(vmf.Direct3DSurface).AsTask();
                    if (!copy.Wait(2000)) { Log("surface copy timed out"); return; }
                    sb = copy.Result;
                    owned = true;
                }
                if (sb == null) return;
                try { Deliver(sb, t.HasValue ? t.Value.Ticks : -1); }
                finally { if (owned) sb.Dispose(); }
            }
        }

        private void Deliver(SoftwareBitmap sb, long ticks)
        {
            CameraPixelFormat fmt;
            switch (sb.BitmapPixelFormat)
            {
                case BitmapPixelFormat.Nv12: fmt = CameraPixelFormat.Nv12; break;
                case BitmapPixelFormat.Yuy2: fmt = CameraPixelFormat.Yuy2; break;
                case BitmapPixelFormat.Bgra8: fmt = CameraPixelFormat.Bgra; break;
                default: return;
            }
            int w = sb.PixelWidth, h = sb.PixelHeight;   // the frame's own size, not the one asked for
            if (w < 2 || h < 2) return;
            using (var buf = sb.LockBuffer(BitmapBufferAccessMode.Read))
            using (var reference = buf.CreateReference())   // disposed before the buffer (the handoff's order)
            {
                IntPtr p; uint cap;
                ((IMemoryBufferByteAccess)reference).GetBuffer(out p, out cap);
                var d0 = buf.GetPlaneDescription(0);
                int rowBytes0 = fmt == CameraPixelFormat.Nv12 ? w : w * (fmt == CameraPixelFormat.Yuy2 ? 2 : 4);
                if ((long)d0.StartIndex + (long)d0.Stride * (h - 1) + rowBytes0 > cap) { Log("plane 0 out of bounds"); return; }
                var f = new CameraFrame { Format = fmt, Width = w, Height = h, Plane0 = p + d0.StartIndex, Stride0 = d0.Stride, Ticks = ticks };
                if (fmt == CameraPixelFormat.Nv12)
                {
                    if (buf.GetPlaneCount() < 2) return;
                    var d1 = buf.GetPlaneDescription(1);
                    if ((long)d1.StartIndex + (long)d1.Stride * (h / 2 - 1) + w > cap) { Log("plane 1 out of bounds"); return; }
                    f.Plane1 = p + d1.StartIndex; f.Stride1 = d1.Stride;
                }
                _frames++;
                var handler = FrameArrived;
                if (handler != null) handler(ref f);
            }   // after this the pointers are dead
        }

        // ── recording ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Starts an H.264 + AAC MP4 of the camera cropped to a centred square, <paramref name="size"/> pixels a side.
        /// If the camera won't record while the frame reader runs, the reader is paused for the recording.</summary>
        public async Task StartRecordingAsync(int size)
        {
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
            profile.Video.Width = (uint)size;
            profile.Video.Height = (uint)size;
            if (!HasAudio) profile.Audio = null;
            await AddSquareCropAsync(size).ConfigureAwait(false);
            _recStream = new InMemoryRandomAccessStream();
            try { await _mc.StartRecordToStreamAsync(profile, _recStream).AsTask().ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log("record with the reader running failed (" + Describe(ex) + ") -> pausing the preview for the recording");
                await StopReaderAsync().ConfigureAwait(false);
                _previewPausedForRecording = true;
                _recStream.Dispose();
                _recStream = new InMemoryRandomAccessStream();
                await _mc.StartRecordToStreamAsync(profile, _recStream).AsTask().ConfigureAwait(false);
            }
            Log("recording " + size + "x" + size + (_previewPausedForRecording ? " (preview paused)" : " (preview live)"));
        }

        private async Task AddSquareCropAsync(int size)
        {
            try
            {
                var props = _mc.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoRecord) as VideoEncodingProperties;
                if (props == null || props.Width < 2 || props.Height < 2) { Log("record stream size unknown: no crop"); return; }
                uint side = Math.Min(props.Width, props.Height) & ~1u;
                var fx = new VideoTransformEffectDefinition
                {
                    CropRectangle = new Rect((props.Width - side) / 2, (props.Height - side) / 2, side, side),
                    OutputSize = new Size(size, size),
                };
                await _mc.AddVideoEffectAsync(fx, MediaStreamType.VideoRecord).AsTask().ConfigureAwait(false);
                Log("record crop " + props.Width + "x" + props.Height + " -> centre " + side + "x" + side + " -> " + size);
            }
            catch (Exception ex) { Log("square crop unavailable (" + Describe(ex) + "): the picture is scaled instead"); }
        }

        /// <summary>Stops and FINALISES the MP4 into <paramref name="outputPath"/>; the preview resumes if it was paused.</summary>
        public async Task StopRecordingAsync(string outputPath)
        {
            try { await _mc.StopRecordAsync().AsTask().ConfigureAwait(false); }
            catch (Exception ex) { Log("StopRecord: " + Describe(ex)); }
            try { await _mc.ClearEffectsAsync(MediaStreamType.VideoRecord).AsTask().ConfigureAwait(false); } catch { }
            try
            {
                if (_recStream != null)
                {
                    _recStream.Seek(0);
                    using (var net = _recStream.AsStreamForRead())
                    using (var fs = File.Create(outputPath))
                        await net.CopyToAsync(fs).ConfigureAwait(false);
                }
            }
            finally { try { if (_recStream != null) _recStream.Dispose(); } catch { } _recStream = null; }
            if (_previewPausedForRecording)
            {
                _previewPausedForRecording = false;
                _frames = 0;
                await StartReaderAsync(FrameWidth, FrameHeight, (int)Math.Round(Fps)).ConfigureAwait(false);
            }
        }

        // ── teardown ─────────────────────────────────────────────────────────────────────────────────────────────────

        private async Task StopReaderAsync()
        {
            var r = _reader;
            _reader = null;
            if (r == null) return;
            try { r.FrameArrived -= OnFrameArrived; } catch { }
            try
            {
                var stop = r.StopAsync().AsTask();
                await Task.WhenAny(stop, Task.Delay(3000)).ConfigureAwait(false);   // capped, never cancelled: the camera must be released
            }
            catch { }
            try { r.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            _arrived.Set();
            try { StopReaderAsync().Wait(3500); } catch { }
            try { if (_pump != null && _pump.IsAlive) _pump.Join(1000); } catch { }
            try { if (_recStream != null) { _mc.StopRecordAsync().AsTask().Wait(2000); _recStream.Dispose(); } } catch { }
            try { if (_mc != null) _mc.Dispose(); } catch { }   // last: releases the camera
            _mc = null;
            Log("disposed after " + _frames + " frames");
        }

        private static string Describe(Exception ex)
        {
            var e = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
            return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + " " + e.Message;
        }

        [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMemoryBufferByteAccess { void GetBuffer(out IntPtr buffer, out uint capacity); }
    }
}
