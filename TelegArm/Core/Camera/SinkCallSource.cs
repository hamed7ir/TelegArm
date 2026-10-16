using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TelegArm.Core.Calls;
using TelegArm.Helpers;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;

namespace TelegArm.Core.Camera
{
    /// <summary>
    /// Windows 8.1: a call's camera without MediaFrameReader. MediaCapture (8.0+) streams its preview into tgcalls_native's
    /// custom Media Foundation sink (StartPreviewToCustomSinkAsync with the sink as an IMediaExtension), which pushes every
    /// sample into the call's camera. The same pipeline the 8.1 round-video recording runs on, ending in our sink instead of
    /// Windows' encoder. Prototyped on the dev box (Windows 11, 30 fps NV12 640x480 from a .NET 4 harness bound to the 8.1
    /// winmd); NOT yet run on RT 8.1. Picks the preview format nearest 640x480 first, so the camera's own type is taken
    /// without a conversion.
    /// </summary>
    internal sealed class SinkCallSource : IDisposable
    {
        private MediaCapture _mc;
        private object _sink;

        private SinkCallSource() { }

        /// <summary>The camera streaming into <paramref name="tgnCamera"/>, or null when no picture arrived within 4 s.</summary>
        public static async Task<SinkCallSource> OpenAsync(string deviceId, IntPtr tgnCamera, Action<string> failed)
        {
            var src = new SinkCallSource { _mc = new MediaCapture() };
            try
            {
                await src._mc.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = deviceId,
                    StreamingCaptureMode = StreamingCaptureMode.Video,   // video only: the call's microphone is the DLL's
                    PhotoCaptureSource = PhotoCaptureSource.Auto,
                }).AsTask().ConfigureAwait(false);
                src._mc.Failed += (s, e) =>
                {
                    Logger.Diag("[CALL-CAM] MediaCapture failed 0x" + e.Code.ToString("X8") + " " + e.Message);
                    if (failed != null) failed(e.Message);
                };

                uint w = 640, h = 480, fps = 30;
                var best = PickPreview(src._mc);
                if (best != null)
                {
                    await src._mc.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, best).AsTask().ConfigureAwait(false);
                    w = best.Width; h = best.Height;
                    if (best.FrameRate.Denominator != 0) fps = Math.Max(1, best.FrameRate.Numerator / best.FrameRate.Denominator);
                    Logger.Diag("[CALL-CAM] sink preview " + best.Subtype + " " + w + "x" + h + " @" + fps);
                }

                IntPtr unk = TgCalls.tgn_camsink_create(tgnCamera, (int)w, (int)h, (int)fps);
                if (unk == IntPtr.Zero) { Logger.Diag("[CALL-CAM] no media sink (Media Foundation missing?)"); src.Dispose(); return null; }
                try { src._sink = Marshal.GetObjectForIUnknown(unk); }
                finally { Marshal.Release(unk); }
                var ext = (IMediaExtension)src._sink;   // QI through the CLR's WinRT interop

                var profile = new MediaEncodingProfile { Audio = null, Container = null };
                profile.Video = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Nv12, w, h);
                profile.Video.FrameRate.Numerator = fps;
                profile.Video.FrameRate.Denominator = 1;
                await src._mc.StartPreviewToCustomSinkAsync(profile, ext).AsTask().ConfigureAwait(false);

                for (int i = 0; i < 40; i++)   // the handoff's rule: no picture within 4 s = this path does not work here
                {
                    TgCalls.CameraStats st;
                    TgCalls.tgn_camera_stats_get(tgnCamera, out st);
                    if (st.Pushed > 0) return src;
                    await Task.Delay(100).ConfigureAwait(false);
                }
                Logger.Diag("[CALL-CAM] media sink: no picture in 4 s");
                src.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                Logger.Diag("[CALL-CAM] media sink start: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                src.Dispose();
                throw;
            }
        }

        /// <summary>The preview format nearest 640x480 at 15 fps or more, among those the sink takes as they are.</summary>
        private static VideoEncodingProperties PickPreview(MediaCapture mc)
        {
            try
            {
                return mc.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview)
                    .OfType<VideoEncodingProperties>()
                    .Where(v => (v.Subtype == "NV12" || v.Subtype == "YUY2" || v.Subtype == "RGB32") && v.Width >= 160 && v.Height >= 120
                                && (v.Width & 1) == 0 && (v.Height & 1) == 0)
                    .OrderBy(v => Math.Abs((int)v.Width - 640) + Math.Abs((int)v.Height - 480)
                                  + (v.Width > 640 || v.Height > 480 ? 100000 : 0)
                                  + (v.FrameRate.Denominator == 0 || v.FrameRate.Numerator / (double)v.FrameRate.Denominator < 14.5 ? 50000 : 0)
                                  + (v.Subtype == "NV12" ? 0 : v.Subtype == "YUY2" ? 1 : 2))
                    .FirstOrDefault();
            }
            catch (Exception ex) { Logger.Diag("[CALL-CAM] preview formats: " + ex.Message); return null; }
        }

        /// <summary>Blocks while the preview stops (3 s at most).</summary>
        public void Dispose()
        {
            var mc = _mc;
            _mc = null;
            if (mc != null)
            {
                try { mc.StopPreviewAsync().AsTask().Wait(3000); } catch { }
                try { mc.Dispose(); } catch { }
            }
            var s = _sink;
            _sink = null;
            if (s != null) try { Marshal.ReleaseComObject(s); } catch { }
        }
    }
}
