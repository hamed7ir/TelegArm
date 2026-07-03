using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace TelegArm.Core.Camera
{
    /// <summary>
    /// WinRT <see cref="MediaCapture"/> backend (the default — proven on RT 8.1: 2 cameras, init OK, frame
    /// decoded). Records via <c>StartRecordToStreamAsync(MediaEncodingProfile.CreateMp4 @ 384×384)</c> → flushes
    /// the finalized MP4 to disk. 8.1 has no <c>GetPreviewFrameAsync</c> and no WinForms CaptureElement, so the
    /// live preview POLLS <c>CapturePhotoToStreamAsync</c> off-thread (the call the probe proved) into a
    /// circular PictureBox; the camera owns the pipeline during recording, so the poll pauses then.
    /// </summary>
    public sealed class WinRtCameraRecorder : ICameraRecorder
    {
        private MediaCapture _mc;
        private readonly List<CameraInfo> _cams = new List<CameraInfo>();
        private string _curId;
        private PictureBox _previewBox;
        private CancellationTokenSource _pollCts;
        private InMemoryRandomAccessStream _recStream;
        private string _outputPath;
        private volatile bool _recording;
        private volatile bool _disposed;

        public IReadOnlyList<CameraInfo> Cameras { get { return _cams; } }
        public event Action<Exception> Failed;

        /// <summary>Enumerate + init + one test frame. True if WinRT capture works here (selection probe).
        /// Leaves the recorder INITIALIZED + ready on success.</summary>
        public async Task<bool> ProbeAsync()
        {
            try
            {
                var found = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask().ConfigureAwait(false);
                _cams.Clear();
                foreach (var d in found)
                {
                    CameraFacing facing = CameraFacing.Unknown;
                    try { if (d.EnclosureLocation != null) facing = d.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front ? CameraFacing.Front : d.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Back ? CameraFacing.Back : CameraFacing.Unknown; } catch { }
                    _cams.Add(new CameraInfo { Id = d.Id, Name = d.Name, Facing = facing });
                }
                System.Diagnostics.Debug.WriteLine("[ROUND-REC] WinRT cameras: " + _cams.Count);
                if (_cams.Count == 0) return false;

                var prefer = _cams.FirstOrDefault(c => c.Facing == CameraFacing.Front) ?? _cams[0];
                await InitCameraAsync(prefer.Id).ConfigureAwait(false);

                using (var ras = new InMemoryRandomAccessStream())
                {
                    await _mc.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), ras).AsTask().ConfigureAwait(false);
                    if (ras.Size == 0) return false;
                }
                System.Diagnostics.Debug.WriteLine("[ROUND-REC] WinRT probe OK (frame produced)");
                return true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ROUND-REC] WinRT probe failed: " + ex.Message); return false; }
        }

        private async Task InitCameraAsync(string id)
        {
            if (_mc != null) { try { _mc.Dispose(); } catch { } _mc = null; }
            _mc = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = id,
                StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo,   // capture the MIC into the same MP4, one pass (CreateMp4 has an AAC track)
                PhotoCaptureSource = PhotoCaptureSource.Auto
            };
            try { await _mc.InitializeAsync(settings).AsTask().ConfigureAwait(false); }
            catch
            {
                // No mic / audio init refused → fall back to video-only so a mic-less device still records (silent).
                try { _mc.Dispose(); } catch { }
                _mc = new MediaCapture();
                settings.StreamingCaptureMode = StreamingCaptureMode.Video;
                await _mc.InitializeAsync(settings).AsTask().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine("[ROUND-REC] WinRT audio init failed → video-only");
            }
            _curId = id;
        }

        public bool TryAttachPreview(Control host)
        {
            try
            {
                _previewBox = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom };
                host.Controls.Add(_previewBox);
                _pollCts = new CancellationTokenSource();
                var ct = _pollCts.Token;
                var ignore = Task.Run(() => PollLoopAsync(ct));
                return true;   // poll attempted; frozen frames + the UI indicator cover the recording phase
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ROUND-REC] preview attach failed: " + ex.Message); return false; }
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                if (_recording || _mc == null) { try { await Task.Delay(300, ct).ConfigureAwait(false); } catch { break; } continue; }
                try
                {
                    using (var ras = new InMemoryRandomAccessStream())
                    {
                        await _mc.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), ras).AsTask().ConfigureAwait(false);
                        ras.Seek(0);
                        int len = (int)ras.Size;
                        if (len > 0)
                        {
                            var bytes = new byte[len];
                            using (var net = ras.AsStreamForRead())
                            {
                                int off = 0, r;
                                while (off < len && (r = await net.ReadAsync(bytes, off, len - off).ConfigureAwait(false)) > 0) off += r;
                            }
                            ShowFrame(bytes);
                        }
                    }
                }
                catch { /* busy / mid-record → skip this frame */ }
                try { await Task.Delay(120, ct).ConfigureAwait(false); } catch { break; }
            }
        }

        private void ShowFrame(byte[] jpeg)
        {
            if (_disposed || _previewBox == null) return;
            try
            {
                Bitmap bmp;
                using (var ms = new MemoryStream(jpeg)) using (var t = Image.FromStream(ms)) bmp = new Bitmap(t);
                if (_previewBox.IsHandleCreated)
                    _previewBox.BeginInvoke((Action)(() =>
                    {
                        if (_disposed || _previewBox == null) { bmp.Dispose(); return; }
                        var old = _previewBox.Image; _previewBox.Image = bmp; if (old != null) old.Dispose();
                    }));
                else bmp.Dispose();
            }
            catch { }
        }

        public async Task SelectCameraAsync(string id)
        {
            if (string.IsNullOrEmpty(id) || id == _curId) return;
            await InitCameraAsync(id).ConfigureAwait(false);
        }

        public async Task SwitchCameraAsync()
        {
            if (_cams.Count < 2) return;
            int i = _cams.FindIndex(c => c.Id == _curId);
            var next = _cams[(i + 1) % _cams.Count];
            await SelectCameraAsync(next.Id).ConfigureAwait(false);
        }

        public async Task StartAsync(string outputPath)
        {
            _outputPath = outputPath;
            _recStream = new InMemoryRandomAccessStream();
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
            try { profile.Video.Width = 384; profile.Video.Height = 384; } catch { }     // square (scaled — 8.1 has no crop)
            _recording = true;
            await Task.Delay(220).ConfigureAwait(false);   // let the preview poll yield the camera
            try { await _mc.StartRecordToStreamAsync(profile, _recStream).AsTask().ConfigureAwait(false); }
            catch (Exception ex) { _recording = false; var h = Failed; if (h != null) h(ex); throw; }
            System.Diagnostics.Debug.WriteLine("[ROUND-REC] WinRT recording started → " + outputPath);
        }

        public async Task<string> StopAsync()
        {
            try { await _mc.StopRecordAsync().AsTask().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ROUND-REC] StopRecord error: " + ex.Message); }
            _recording = false;
            try
            {
                if (_recStream != null)
                {
                    _recStream.Seek(0);
                    using (var net = _recStream.AsStreamForRead())
                    using (var fs = File.Create(_outputPath))
                        await net.CopyToAsync(fs).ConfigureAwait(false);
                }
            }
            finally { try { if (_recStream != null) _recStream.Dispose(); } catch { } _recStream = null; }
            return _outputPath;
        }

        public void Dispose()
        {
            _disposed = true;
            try { if (_pollCts != null) _pollCts.Cancel(); } catch { }
            if (_recording) { try { _mc.StopRecordAsync().AsTask().Wait(2000); } catch { } _recording = false; }
            try { if (_mc != null) _mc.Dispose(); } catch { }      // releases the camera
            try { if (_recStream != null) _recStream.Dispose(); } catch { }
            try { if (_previewBox != null && _previewBox.Image != null) { _previewBox.Image.Dispose(); } } catch { }
            _mc = null;
            System.Diagnostics.Debug.WriteLine("[ROUND-REC] WinRT recorder disposed (camera released)");
        }
    }
}
