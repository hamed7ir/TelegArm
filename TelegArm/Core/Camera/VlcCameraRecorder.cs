using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace TelegArm.Core.Camera
{
    /// <summary>
    /// libVLC DirectShow backend — the FALLBACK when WinRT MediaCapture is absent/broken (Windows 7 in
    /// practice). Captures <c>dshow://</c> and records via a <c>:sout</c> transcode (h264 384×384 + AAC mono,
    /// mux=mp4) — chain verified present in the bundled VLC. Previews the live camera in a VideoView (and keeps
    /// previewing while recording via <c>#duplicate{dst=display,…}</c>). dshow camera enumeration isn't exposed
    /// by LibVLCSharp, so this uses the DEFAULT camera (no Front/Back flip) — acceptable for the Win7 fallback.
    /// </summary>
    public sealed class VlcCameraRecorder : ICameraRecorder
    {
        private LibVLC _libvlc;
        private MediaPlayer _player;
        private VideoView _view;
        private string _outputPath;
        private readonly List<CameraInfo> _cams = new List<CameraInfo> { new CameraInfo { Id = "", Name = "Default camera", Facing = CameraFacing.Unknown } };

        public IReadOnlyList<CameraInfo> Cameras { get { return _cams; } }
        public event Action<Exception> Failed;

        /// <summary>Selection probe: libVLC present + initializes. The actual capture is verified at record time
        /// (a heavier dshow capture-test is skipped — this is the rarely-used Win7 fallback path).</summary>
        public Task<bool> ProbeAsync()
        {
            bool ok = false;
            try
            {
                if (VlcEnvironment.TryInitialize()) { _libvlc = new LibVLC(); _player = new MediaPlayer(_libvlc); ok = true; }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ROUND-REC] VLC probe failed: " + ex.Message); }
            return Task.FromResult(ok);
        }

        public bool TryAttachPreview(Control host)
        {
            try
            {
                _view = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black, MediaPlayer = _player };
                host.Controls.Add(_view);
                if (_player != null) _player.EncounteredError += (s, e) => { var h = Failed; if (h != null) h(new Exception("VLC capture error")); };
                PlayPreviewOnly();
                return true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ROUND-REC] VLC preview attach failed: " + ex.Message); return false; }
        }

        private void PlayPreviewOnly()
        {
            if (_libvlc == null || _player == null) return;
            using (var media = new Media(_libvlc, "dshow://", FromType.FromLocation))
            {
                media.AddOption(":dshow-vdev=");
                media.AddOption(":dshow-adev=none");
                media.AddOption(":live-caching=120");
                _player.Play(media);
            }
        }

        public Task SelectCameraAsync(string id) { return Task.FromResult(0); }   // single default camera
        public Task SwitchCameraAsync() { System.Diagnostics.Debug.WriteLine("[ROUND-REC] VLC: flip n/a (default camera)"); return Task.FromResult(0); }

        public Task StartAsync(string outputPath)
        {
            _outputPath = outputPath;
            string dst = outputPath.Replace('\\', '/');
            string sout = ":sout=#duplicate{dst=display,dst=transcode{vcodec=h264,vb=1000,fps=25,width=384,height=384,acodec=mp4a,ab=64,channels=1,samplerate=44100}:standard{access=file,mux=mp4,dst='" + dst + "'}}";
            try { _player.Stop(); } catch { }
            var media = new Media(_libvlc, "dshow://", FromType.FromLocation);
            media.AddOption(":dshow-vdev=");
            media.AddOption(":dshow-adev=");          // default audio device → capture the mic
            media.AddOption(":live-caching=120");
            media.AddOption(sout);
            media.AddOption(":sout-keep");
            _player.Play(media);
            media.Dispose();
            System.Diagnostics.Debug.WriteLine("[ROUND-REC] VLC recording started → " + outputPath);
            return Task.FromResult(0);
        }

        public async Task<string> StopAsync()
        {
            try { _player.Stop(); } catch { }            // closes the sout chain → mux_mp4 writes the moov
            await Task.Delay(400).ConfigureAwait(false); // brief flush so the MP4 is finalized on disk
            return _outputPath;
        }

        public void Dispose()
        {
            try { if (_player != null) _player.Stop(); } catch { }
            try { if (_view != null) _view.MediaPlayer = null; } catch { }
            try { if (_player != null) _player.Dispose(); } catch { }
            try { if (_libvlc != null) _libvlc.Dispose(); } catch { }
            try { if (_view != null) _view.Dispose(); } catch { }
            _player = null; _libvlc = null;
            System.Diagnostics.Debug.WriteLine("[ROUND-REC] VLC recorder disposed (camera released)");
        }
    }
}
