using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TelegArm.Core.Camera;
using TelegArm.Core.Media;
using TelegArm.Helpers;
using Windows.Devices.Enumeration;

namespace TelegArm.Core.Calls
{
    /// <summary>
    /// The camera a call sends (2026-10-16): a native tgn_camera fed by one of two backends -
    ///  * Windows 10 1607+: the round-video camera's MediaFrameReader (TelegArm.Camera10, measured 30 fps on the Surface by
    ///    the Venetium probe), its NV12/YUY2 planes pushed straight in;
    ///  * Windows 8.1 (no MediaFrameReader): MediaCapture.StartPreviewToCustomSinkAsync into tgcalls_native's media sink.
    /// The DLL converts (I420, scaled down to 1280x720 at most) and hands a preview (mirrored for a front camera) to
    /// <see cref="CallVideoSink"/>.
    /// Opening and closing block on the camera: never on the UI thread.
    /// </summary>
    internal sealed class CallCamera : IDisposable
    {
        public IntPtr Handle { get; private set; }
        public IReadOnlyList<CameraInfo> Cameras { get; private set; }
        public CameraInfo Current { get; private set; }
        public string Backend { get; private set; }
        /// <summary>The camera stopped by itself (taken by another app, unplugged): a reason. On a camera thread.</summary>
        public event Action<string> Failed;
        private IDisposable _source;
        private CallVideoSink _preview;   // its delegate is what the DLL calls: alive as long as this camera
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _disposed, _holdsDevice;

        /// <summary>One call camera at a time, process-wide: a new one waits until the last has let go of the device (its
        /// close takes a moment), instead of failing "busy" on a quick redial or Stop video / Start video.</summary>
        private static readonly SemaphoreSlim Device = new SemaphoreSlim(1, 1);

        /// <summary>Debug override: TELEGARM_CALL_CAMERA=sink uses the Windows 8.1 media sink on Windows 10 too (the dev-box
        /// test of the 8.1 path), =reader the frame reader only.</summary>
        internal static string Forced
        {
            get { try { return Environment.GetEnvironmentVariable("TELEGARM_CALL_CAMERA"); } catch { return null; } }
        }

        private CallCamera() { }

        public static async Task<List<CameraInfo>> ListAsync()
        {
            var found = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask().ConfigureAwait(false);
            var list = new List<CameraInfo>();
            foreach (var d in found)
            {
                var facing = CameraFacing.Unknown;
                try
                {
                    if (d.EnclosureLocation != null)
                        facing = d.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front ? CameraFacing.Front
                               : d.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Back ? CameraFacing.Back : CameraFacing.Unknown;
                }
                catch { }
                list.Add(new CameraInfo { Id = d.Id, Name = d.Name, Facing = facing });
            }
            return list;
        }

        /// <summary>Opens the front camera (or the first) and starts it; throws with a sentence for the user when it can't.</summary>
        public static Task<CallCamera> OpenAsync(CallVideoSink preview)
        {
            return Task.Run(async () =>
            {
                if (!TgCalls.Available) throw new InvalidOperationException("Calls are unavailable here.");
                if (!await Device.WaitAsync(8000).ConfigureAwait(false)) throw new InvalidOperationException("The camera is still closing.");
                var cam = new CallCamera { _holdsDevice = true, _preview = preview };
                try
                {
                    var cams = await ListAsync().ConfigureAwait(false);
                    Logger.Diag("[CALL-CAM] " + cams.Count + " camera(s): " + string.Join(", ", cams.Select(c => c.Name + " (" + c.Facing + ")")));
                    if (cams.Count == 0) throw new InvalidOperationException("No camera was found.");
                    cam.Cameras = cams;
                    cam.Handle = TgCalls.tgn_camera_create(1280, 720);
                    if (cam.Handle == IntPtr.Zero) throw new InvalidOperationException("The call library couldn't make a camera.");
                    var first = cams.FirstOrDefault(c => c.Facing == CameraFacing.Front) ?? cams[0];
                    string why;
                    try { why = await cam.StartAsync(first).ConfigureAwait(false); }
                    catch (Exception ex) { why = Problem(ex); }
                    if (why != null) throw new InvalidOperationException(why);
                    return cam;
                }
                catch { cam.Dispose(); throw; }   // also lets go of the device
            });
        }

        /// <summary>The next camera (front, back). 1 switched; 0 the next would not start and the old one runs again; -1 neither
        /// runs now (the camera is off).</summary>
        public Task<int> SwitchAsync()
        {
            return Task.Run(async () =>
            {
                if (Cameras == null || Cameras.Count < 2 || Current == null) return 0;
                var old = Current;
                var next = Cameras[(Cameras.ToList().IndexOf(old) + 1) % Cameras.Count];
                string why = await StartAsync(next).ConfigureAwait(false);
                if (why == null) return 1;
                Logger.Diag("[CALL-CAM] switch to " + next.Name + " failed: " + why);
                return await StartAsync(old).ConfigureAwait(false) == null ? 0 : -1;
            });
        }

        /// <summary>Stops whatever streams now and starts <paramref name="info"/>; null on success, else why not.</summary>
        private async Task<string> StartAsync(CameraInfo info)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return "The camera is closed.";   // a switch's restore that lost the race to Dispose
                var old = _source;
                _source = null;
                Current = null;
                if (old != null) try { old.Dispose(); } catch (Exception ex) { Logger.Diag("[CALL-CAM] stop: " + ex.Message); }
                // the preview is a mirror for a front camera, as everyone expects; a rear camera shows the scene as it is
                if (_preview != null)
                    TgCalls.tgn_camera_set_preview(Handle, _preview.Callback, IntPtr.Zero, 480, 480, 15, info.Facing == CameraFacing.Back ? 0 : 1);
                Action<string> failed = why => { var h = Failed; if (h != null) h(why); };
                string forced = Forced;
                bool reader = forced != "sink" && CameraCapture.FrameReaderPresent;
                string problem = null;
                if (reader)
                {
                    var src = await OpenReaderAsync(info.Id, Handle, failed).ConfigureAwait(false);
                    if (src != null) { _source = src; Current = info; Backend = "frame reader"; }
                    else problem = "The camera didn't start.";
                    if (src != null || forced == "reader") return problem;
                }
                var sink = await SinkCallSource.OpenAsync(info.Id, Handle, failed).ConfigureAwait(false);   // Windows 8.1's way
                if (sink == null) return problem ?? "The camera didn't start.";
                _source = sink; Current = info; Backend = "media sink";
                return null;
            }
            catch (Exception ex) { return Problem(ex); }
            finally
            {
                _gate.Release();
                if (Current != null) Logger.Diag("[CALL-CAM] " + Current.Name + " via " + (Backend ?? "-"));
            }
        }

        /// <summary>Only reached on Windows 10 1607+: the one place that names the Camera10 backend (JIT-resolved when
        /// called, so 8.1 never loads it).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Task<IDisposable> OpenReaderAsync(string id, IntPtr handle, Action<string> failed) { return FrameReaderCallSource.OpenAsync(id, handle, failed); }

        internal static string Problem(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "Windows doesn't allow this app to use the camera (privacy settings).";
            int hr = ex.HResult;
            if (hr == unchecked((int)0xC00D3704) || hr == unchecked((int)0x80070020) || hr == unchecked((int)0xC00DABE4))
                return "The camera is busy (another app, or a round video being recorded).";
            if (ex is InvalidOperationException) return ex.Message;
            return "The camera didn't start (" + ex.GetType().Name + " 0x" + hr.ToString("X8") + ").";
        }

        public TgCalls.CameraStats Stats()
        {
            TgCalls.CameraStats s = default(TgCalls.CameraStats);
            if (Handle != IntPtr.Zero) TgCalls.tgn_camera_stats_get(Handle, out s);
            return s;
        }

        /// <summary>Blocks while the camera stops (up to a few seconds). Safe to call twice.</summary>
        public void Dispose()
        {
            _gate.Wait();
            try
            {
                _disposed = true;
                var src = _source;
                _source = null;
                if (src != null) try { src.Dispose(); } catch (Exception ex) { Logger.Diag("[CALL-CAM] dispose: " + ex.Message); }
                var h = Handle;
                Handle = IntPtr.Zero;
                if (h != IntPtr.Zero) TgCalls.tgn_camera_destroy(h);   // no preview callback after this returns
                if (_holdsDevice) { _holdsDevice = false; Device.Release(); }
            }
            finally { _gate.Release(); }
            GC.KeepAlive(_preview);
        }
    }
}
