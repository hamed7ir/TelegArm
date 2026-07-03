using System;
using System.IO;   // needed: WindowsRuntimeStreamExtensions.AsStreamForRead (extension method) resolves via this namespace
using System.Drawing;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using TelegArm.Core;
using TelegArm.Helpers;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace TelegArm.UI
{
    /// <summary>
    /// PROBE-ONLY (debug): can this app CAPTURE the camera on RT 8.1 ARM32? Two probes, side by side:
    /// (1) libVLC DirectShow (dshow://), (2) WinRT <c>Windows.Media.Capture.MediaCapture</c> — RT's native
    /// camera path (the API the built-in Camera app uses). The WinRT probe enumerates cameras, initializes
    /// MediaCapture, and captures one frame; each step logs [WINRT-PROBE] success or the exact exception, so
    /// the RT run tells us EXACTLY which sub-question is the wall (reference / init-sandbox / frame). No
    /// recording — the result decides whether record/send is feasible. Run on RT; the log is the answer.
    /// </summary>
    public sealed class RoundProbeForm : Form
    {
        private LibVLC _libvlc;
        private MediaPlayer _player;
        private VideoView _view;
        private PictureBox _photo;
        private Label _status;
        private TextBox _log;
        private System.Windows.Forms.Timer _timer;
        private int _ticks;

        public RoundProbeForm()
        {
            Text = "Camera capture probe (debug)";
            TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in the taskbar / Alt-Tab
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 540);
            MinimizeBox = false; MaximizeBox = false;
            bool dark = ThemeHelper.IsDark;
            BackColor = dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(245, 245, 247);
            Color fg = dark ? Color.White : Color.FromArgb(20, 20, 20);

            _status = new Label { Left = 12, Top = 10, Width = 536, Height = 22, ForeColor = fg, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Text = "Pick a probe." };

            var dshowBtn = new Button { Text = "DirectShow capture", Left = 12, Top = 36, Width = 260, Height = 30 };
            dshowBtn.Click += (s, e) => RunDshowProbe();
            var winrtBtn = new Button { Text = "WinRT MediaCapture", Left = 288, Top = 36, Width = 260, Height = 30 };
            winrtBtn.Click += (s, e) => RunWinRtProbe();

            var preview = new System.Windows.Forms.Panel { Left = 12, Top = 74, Width = 536, Height = 240, BackColor = Color.Black };
            _view = new VideoView { Dock = DockStyle.Fill, BackColor = Color.Black, Visible = false };
            _photo = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
            preview.Controls.Add(_view); preview.Controls.Add(_photo);

            _log = new TextBox { Left = 12, Top = 322, Width = 536, Height = 158, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = dark ? Color.FromArgb(24, 24, 28) : Color.White, ForeColor = fg, Font = new Font("Consolas", 8.5f) };
            var close = new Button { Text = "Close", Left = 458, Top = 490, Width = 90, Height = 30 };
            close.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { _status, dshowBtn, winrtBtn, preview, _log, close });

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += DshowTick;
        }

        private void Log(string s)
        {
            System.Diagnostics.Debug.WriteLine(s);
            try { _log.AppendText(s + "\r\n"); } catch { }
        }

        // ───────────────────────── DirectShow (libVLC) probe ─────────────────────────
        private void RunDshowProbe()
        {
            _photo.Visible = false; _view.Visible = true; _ticks = 0;
            Log("[ROUND-PROBE] === DirectShow (libVLC) probe ===");
            try
            {
                if (!VlcEnvironment.TryInitialize())
                {
                    _status.Text = "libVLC unavailable — DirectShow not testable here.";
                    Log("[ROUND-PROBE] libVLC init FAILED → capture NOT testable");
                    return;
                }
                if (_libvlc == null) _libvlc = new LibVLC();
                Log("[ROUND-PROBE] libVLC initialized.");
                try
                {
                    var discs = _libvlc.MediaDiscoverers(MediaDiscovererCategory.Devices);
                    Log("[ROUND-PROBE] device discoverers: " + (discs != null ? discs.Length : 0));
                    if (discs != null) foreach (var d in discs) Log("[ROUND-PROBE]   discoverer: " + d.Name + " (" + d.LongName + ")");
                }
                catch (Exception ex) { Log("[ROUND-PROBE] enumeration error: " + ex.Message); }

                if (_player == null) { _player = new MediaPlayer(_libvlc); _view.MediaPlayer = _player; }
                using (var media = new Media(_libvlc, "dshow://", FromType.FromLocation))
                {
                    media.AddOption(":dshow-vdev=");
                    media.AddOption(":dshow-adev=none");
                    media.AddOption(":live-caching=200");
                    Log("[ROUND-PROBE] opening dshow:// (default camera)…");
                    _status.Text = "DirectShow: opening camera…";
                    _player.Play(media);
                }
                _timer.Start();
            }
            catch (Exception ex) { _status.Text = "DirectShow error: " + ex.Message; Log("[ROUND-PROBE] exception: " + ex.Message); }
        }

        private void DshowTick(object sender, EventArgs e)
        {
            _ticks++;
            try
            {
                if (_player == null) { _timer.Stop(); return; }
                int vout = (int)_player.VoutCount;
                var st = _player.State;
                Log("[ROUND-PROBE] t+" + _ticks + "s: state=" + st + " voutCount=" + vout);
                if (vout > 0) { _status.Text = "✔ DirectShow works — frames flowing (vout=" + vout + ")."; Log("[ROUND-PROBE] RESULT: frames FLOWING → dshow capture WORKS"); _timer.Stop(); }
                else if (st == VLCState.Error) { _status.Text = "✘ DirectShow capture failed."; Log("[ROUND-PROBE] RESULT: capture FAILED (Error)"); _timer.Stop(); }
                else if (_ticks >= 7) { _status.Text = "✘ DirectShow: no frames after 7s."; Log("[ROUND-PROBE] RESULT: NO frames after 7s"); _timer.Stop(); }
            }
            catch (Exception ex) { Log("[ROUND-PROBE] tick error: " + ex.Message); _timer.Stop(); }
        }

        // ───────────────────────── WinRT MediaCapture probe ─────────────────────────
        private async void RunWinRtProbe()
        {
            _view.Visible = false; _photo.Visible = true; _photo.Image = null;
            _timer.Stop();
            Log("[WINRT-PROBE] === WinRT MediaCapture probe ===");
            MediaCapture mc = null;
            try
            {
                // (1) ENUMERATE — does WinRT see the camera the built-in Camera app uses?
                _status.Text = "WinRT: enumerating cameras…";
                Log("[WINRT-PROBE] DeviceInformation.FindAllAsync(VideoCapture)…");
                var cams = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask();
                Log("[WINRT-PROBE] cameras enumerated: " + cams.Count);
                foreach (var c in cams) Log("[WINRT-PROBE]   camera: " + c.Name + " [" + c.Id + "]");
                if (cams.Count == 0) { _status.Text = "✘ WinRT: no camera enumerated."; Log("[WINRT-PROBE] RESULT: no camera via WinRT (wall: enumeration)"); return; }

                // (2) INIT — does MediaCapture initialize outside a Store sandbox on jailbroken RT?
                _status.Text = "WinRT: initializing MediaCapture…";
                Log("[WINRT-PROBE] new MediaCapture(); InitializeAsync(video, " + cams[0].Name + ")…");
                mc = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = cams[0].Id,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    PhotoCaptureSource = PhotoCaptureSource.Auto
                };
                await mc.InitializeAsync(settings).AsTask();
                Log("[WINRT-PROBE] MediaCapture INITIALIZED OK (no sandbox/identity wall here)");

                // (3) CAPTURE A FRAME — can we get pixels out? (8.1 has no MediaFrameReader → photo-to-stream)
                _status.Text = "WinRT: capturing a frame…";
                Log("[WINRT-PROBE] CapturePhotoToStreamAsync(JPEG → memory)…");
                using (var ras = new InMemoryRandomAccessStream())
                {
                    await mc.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), ras).AsTask();
                    ulong size = ras.Size;
                    Log("[WINRT-PROBE] frame captured: " + size + " bytes");
                    if (size == 0) { _status.Text = "✘ WinRT: init OK but empty frame."; Log("[WINRT-PROBE] RESULT: init OK, NO frame bytes (wall: frame extraction)"); return; }
                    ras.Seek(0);
                    using (var net = ras.AsStreamForRead())
                    using (var img = Image.FromStream(net))
                    {
                        var bmp = new Bitmap(img);
                        if (_photo.Image != null) { var old = _photo.Image; _photo.Image = null; old.Dispose(); }
                        _photo.Image = bmp;
                        Log("[WINRT-PROBE] frame decoded: " + bmp.Width + "x" + bmp.Height);
                    }
                    _status.Text = "✔ WinRT capture WORKS — frame " + size + " bytes.";
                    Log("[WINRT-PROBE] RESULT: WinRT MediaCapture WORKS → recording feasible (capture→encode→send)");
                }
            }
            catch (Exception ex)
            {
                _status.Text = "✘ WinRT probe failed: " + ex.Message;
                Log("[WINRT-PROBE] EXCEPTION: " + ex.GetType().FullName);
                Log("[WINRT-PROBE]   message: " + ex.Message);
                Log("[WINRT-PROBE]   HRESULT: 0x" + ex.HResult.ToString("X8"));
                Log("[WINRT-PROBE] RESULT: WinRT capture BLOCKED here — the exception names the wall (init=sandbox/identity, or frame).");
            }
            finally { try { mc?.Dispose(); } catch { } }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            try { _timer.Stop(); } catch { }
            try { _player?.Stop(); } catch { }
            try { if (_view != null) _view.MediaPlayer = null; } catch { }
            try { _player?.Dispose(); } catch { }
            try { _libvlc?.Dispose(); } catch { }
            try { if (_photo != null && _photo.Image != null) { _photo.Image.Dispose(); _photo.Image = null; } } catch { }
        }
    }
}
