using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using TelegArm.Core;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Hosts the embedded VLC video surface (or a fallback when VLC is unavailable).
    /// Playback is driven by the host via the public API; the host owns the control
    /// bar so it is never painted over by the native video window.
    /// </summary>
    public class VlcVideoControl : UserControl
    {
        private readonly bool _vlcReady;
        private LibVLC _libVlc;
        private MediaPlayer _player;
        private VideoView _videoView;

        private Panel _videoHost;
        private Label _loadingLabel;
        private Label _statusLabel;
        private Panel _fallbackPanel;
        private Label _fallbackLabel;
        private string _currentFile;

        public bool IsReady { get { return _vlcReady; } }
        public bool IsPlaying { get { return _player != null && _player.IsPlaying; } }
        public long Time { get { return _player != null ? _player.Time : 0; } }
        public long Length { get { return _player != null ? _player.Length : 0; } }
        public VLCState State { get { return _player != null ? _player.State : VLCState.NothingSpecial; } }
        /// <summary>STORIES-BUILD-3: true once playback reached the end (so a host can advance without importing VLC types).</summary>
        public bool HasEnded { get { return _player != null && _player.State == VLCState.Ended; } }

        public float Position
        {
            get { return _player != null ? _player.Position : 0f; }
            set { if (_player != null) _player.Position = value; }
        }

        public int Volume { set { if (_player != null) _player.Volume = value; } }

        public VlcVideoControl()
        {
            BackColor = Color.FromArgb(18, 18, 18);
            BuildUi();

            try
            {
                if (VlcEnvironment.TryInitialize())
                {
                    _libVlc = new LibVLC();
                    _player = new MediaPlayer(_libVlc);
                    _videoView = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black, MediaPlayer = _player };
                    _videoHost.Controls.Add(_videoView);
                    _videoView.SendToBack();
                    _loadingLabel.BringToFront();
                    _vlcReady = true;
                }
            }
            catch
            {
                _vlcReady = false; // wrong-arch / init failure → fallback path
            }
        }

        private void BuildUi()
        {
            _videoHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Visible = false };
            _loadingLabel = new Label
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Loading…",
                Visible = false
            };
            _videoHost.Controls.Add(_loadingLabel);

            _statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 18, 18),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false
            };

            _fallbackPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 18, 18), Visible = false };
            _fallbackLabel = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
            var openBtn = new Button
            {
                Text = "Open with system player",
                Dock = DockStyle.Bottom,
                Height = 44,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White
            };
            openBtn.FlatAppearance.BorderColor = Color.Gray;
            openBtn.Click += (s, e) => OpenExternally();
            _fallbackPanel.Controls.Add(_fallbackLabel);
            _fallbackPanel.Controls.Add(openBtn);

            Controls.Add(_videoHost);
            Controls.Add(_fallbackPanel);
            Controls.Add(_statusLabel);
        }

        /// <summary>Shows a centered status message (downloading / error).</summary>
        public void ShowStatus(string text)
        {
            _statusLabel.Text = text;
            _statusLabel.Visible = true;
            _statusLabel.BringToFront();
            _videoHost.Visible = false;
            _fallbackPanel.Visible = false;
        }

        /// <summary>Plays a local file embedded, or shows the fallback if VLC is unavailable.
        /// <paramref name="loop"/> repeats playback (for GIFs).</summary>
        private bool _ensureAudio, _audioEnsured;

        public void PlayFile(string path, bool loop = false, bool ensureAudio = false)
        {
            _currentFile = path;
            _ensureAudio = ensureAudio; _audioEnsured = false;
            if (!_vlcReady) { ShowFallback(); return; }

            _statusLabel.Visible = false;
            _fallbackPanel.Visible = false;
            _videoHost.Visible = true;
            _videoHost.BringToFront();
            _loadingLabel.Visible = true;
            _loadingLabel.BringToFront();

            using (var media = new Media(_libVlc, new Uri(path)))
            {
                if (loop) media.AddOption(":input-repeat=65535");   // GIFs loop forever
                _player.Play(media);
            }
        }

        public void TogglePlay()
        {
            if (_player == null) return;
            if (_player.IsPlaying) _player.Pause(); else _player.Play();
        }

        /// <summary>Toggles mute and returns the new mute state.</summary>
        public bool ToggleMute()
        {
            if (_player == null) return false;
            _player.Mute = !_player.Mute;
            return _player.Mute;
        }

        public void StopPlayback()
        {
            try
            {
                _player?.Stop();
                if (_loadingLabel != null) _loadingLabel.Visible = false;
            }
            catch { }
        }

        /// <summary>Toggles the Loading overlay based on player state. Called by the host timer.</summary>
        public void RefreshOverlay()
        {
            if (_player == null || _loadingLabel == null) return;
            var st = _player.State;
            bool loading = st == VLCState.Opening || st == VLCState.Buffering;
            if (_loadingLabel.Visible != loading)
            {
                _loadingLabel.Visible = loading;
                if (loading) _loadingLabel.BringToFront();
            }
            // Round videos came back silent though the file has audio (it plays in Telegram Desktop) — VLC wasn't
            // auto-playing the track on this path. Once playing, explicitly turn audio ON (unmute + volume +
            // select the audio track). One-shot, round-scoped — regular videos are untouched.
            if (_ensureAudio && !_audioEnsured && _player.IsPlaying)
            {
                _audioEnsured = true;
                EnsureAudioOn();
            }
        }

        /// <summary>Forces audio output on for the current playback: unmute, full volume, and select the first
        /// real audio track if VLC didn't auto-select one.</summary>
        private void EnsureAudioOn()
        {
            try
            {
                _player.Mute = false;
                _player.Volume = 100;
                if (_player.AudioTrack < 0)
                {
                    var tracks = _player.AudioTrackDescription;
                    if (tracks != null)
                        foreach (var t in tracks)
                            if (t.Id >= 0) { _player.SetAudioTrack(t.Id); break; }
                }
                System.Diagnostics.Debug.WriteLine("[ROUND-REC] round playback: audio ensured (track=" + _player.AudioTrack + " vol=" + _player.Volume + " mute=" + _player.Mute + ")");
            }
            catch { }
        }

        private void ShowFallback()
        {
            var arch = Environment.Is64BitProcess ? "x64" : "x86";
            _fallbackLabel.Text =
                "Embedded video playback requires VLC libraries.\n\n" +
                "Place the matching VLC files in a 'libvlc' folder next to TelegArm.exe:\n\n" +
                "• Windows x86 (32-bit PC):\n" +
                "    Download VLC 32-bit from videolan.org\n" +
                "    Copy: libvlc.dll, libvlccore.dll, plugins\\\n\n" +
                "• Windows ARM32 (Surface RT, Windows RT 8.1):\n" +
                "    Use the included ARM32 VLC 3.0.20 build\n" +
                "    Copy: libvlc.dll, libvlccore.dll, plugins\\\n\n" +
                "This TelegArm process is running as: " + arch;
            _fallbackPanel.Visible = true;
            _fallbackPanel.BringToFront();
            _statusLabel.Visible = false;
            _videoHost.Visible = false;
        }

        private void OpenExternally()
        {
            try { if (!string.IsNullOrEmpty(_currentFile)) Process.Start(_currentFile); }
            catch { /* no association / blocked */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Safe order: stop → detach VideoView → player → libvlc → videoview.
                try { _player?.Stop(); } catch { }
                try { if (_videoView != null) _videoView.MediaPlayer = null; } catch { }
                try { _player?.Dispose(); } catch { }
                try { _libVlc?.Dispose(); } catch { }
                try { _videoView?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
