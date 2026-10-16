using System;
using System.Threading.Tasks;
using TelegArm.Camera10;
using TelegArm.Core.Calls;
using TelegArm.Helpers;

namespace TelegArm.Core.Camera
{
    /// <summary>
    /// Windows 10: a call's camera from the round-video frame reader (TelegArm.Camera10). Its raw NV12/YUY2/BGRA planes go
    /// into tgcalls_native as they arrive, on the reader's pump thread: the DLL converts before it returns, so the WinRT
    /// buffer may die after. Named only from <see cref="CallCamera"/> once <see cref="CameraCapture.FrameReaderPresent"/>.
    /// </summary>
    internal sealed class FrameReaderCallSource : IDisposable
    {
        private readonly FrameReaderCamera _cam;
        private readonly IntPtr _tgn;
        private readonly object _push = new object();
        private bool _closed;

        private FrameReaderCallSource(FrameReaderCamera cam, IntPtr tgn) { _cam = cam; _tgn = tgn; }

        /// <summary>The camera streaming into <paramref name="tgnCamera"/>, or null when it doesn't start. Video only: the
        /// call's microphone is the DLL's own (WinMM).</summary>
        public static async Task<IDisposable> OpenAsync(string deviceId, IntPtr tgnCamera, Action<string> failed)
        {
            var cam = await FrameReaderCamera.OpenAsync(deviceId, false, 640, 480, 30, s => Logger.Diag(s)).ConfigureAwait(false);
            if (cam == null) return null;
            var src = new FrameReaderCallSource(cam, tgnCamera);
            cam.FrameArrived += src.OnFrame;
            cam.Failed += ex => { Logger.Diag("[CALL-CAM] frame reader failed: " + ex.Message); if (failed != null) failed(ex.Message); };
            Logger.Diag("[CALL-CAM] frame reader " + cam.FrameWidth + "x" + cam.FrameHeight + " " + cam.Subtype + " @" + cam.Fps.ToString("0.#"));
            return src;
        }

        private void OnFrame(ref CameraFrame f)
        {
            var fin = new TgCalls.FrameIn
            {
                Format = f.Format == CameraPixelFormat.Nv12 ? TgCalls.PixNv12 : f.Format == CameraPixelFormat.Yuy2 ? TgCalls.PixYuy2 : TgCalls.PixBgra,
                Width = f.Width, Height = f.Height,
                Plane0 = f.Plane0, Stride0 = f.Stride0,
                Plane1 = f.Plane1, Stride1 = f.Stride1,
            };
            lock (_push) if (!_closed) TgCalls.tgn_camera_push(_tgn, ref fin);   // never after Dispose: the camera may be gone
        }

        public void Dispose()
        {
            lock (_push) _closed = true;
            _cam.FrameArrived -= OnFrame;
            _cam.Dispose();   // blocks while the reader stops (capped inside)
        }
    }
}
