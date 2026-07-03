using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TL;
using TelegArm.Core;
using TelegArm.Core.Camera;
using TelegArm.Helpers;

namespace TelegArm.UI
{
    /// <summary>
    /// Telegram-style round-video recorder. Backend-BLIND: talks only to <see cref="ICameraRecorder"/> (WinRT
    /// MediaCapture by default, libVLC fallback) — no backend branches here. A circular live preview (or a
    /// recording indicator if the backend can't preview), tap-to-start/tap-to-stop record, Front/Back flip,
    /// a duration counter (≤60s), and Send/Retake/Cancel. The camera is released on close/cancel.
    /// </summary>
    public sealed class RoundRecorderForm : Form
    {
        private readonly TelegramService _service;
        private readonly InputPeer _peer;
        private readonly bool _dark;
        private readonly Color _accent;

        private ICameraRecorder _rec;
        private Panel _previewHost;
        private Label _hint, _duration;
        private Button _recordBtn, _flipBtn, _sendBtn, _cancelBtn;
        private System.Windows.Forms.Timer _timer;
        private int _seconds;
        private const int MaxSeconds = 60;
        private const int Circle = 280;

        private string _tempPath, _recordedPath;
        private bool _hasPreview, _sending;
        private TL.Message _sentMessage;
        /// <summary>The sent round-video Message (set on a successful send) so the caller can append it live.</summary>
        public TL.Message SentMessage { get { return _sentMessage; } }
        private enum St { Init, Idle, Recording, Recorded }
        private St _state = St.Init;

        public RoundRecorderForm(TelegramService service, InputPeer peer)
        {
            _service = service; _peer = peer;
            _dark = ThemeHelper.IsDark;
            _accent = ThemeHelper.GetWindowsAccentColor();
            BuildUi();
            Shown += OnShownAsync;
            FormClosed += (s, e) => Cleanup();
        }

        private void BuildUi()
        {
            Text = "Round video";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(360, 470 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, "Round video", _accent, _dark);   // accent title bar + dark chrome
            Color fg = _dark ? Color.White : Color.FromArgb(20, 20, 20);

            _previewHost = new Panel { Left = (360 - Circle) / 2, Top = 24, Width = Circle, Height = Circle, BackColor = Color.Black };
            using (var p = new GraphicsPath()) { p.AddEllipse(0, 0, Circle, Circle); _previewHost.Region = new Region(p); }
            _previewHost.Paint += (s, e) =>   // shown until/unless a live preview is attached
            {
                if (_hasPreview) return;
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Color.FromArgb(50, 50, 54))) g.FillEllipse(b, 0, 0, Circle, Circle);
                bool rec = _state == St.Recording;
                using (var rb = new SolidBrush(rec ? Color.FromArgb(230, 60, 60) : Color.FromArgb(120, 120, 128)))
                    g.FillEllipse(rb, Circle / 2 - 12, Circle / 2 - 28, 24, 24);
                TextRenderer.DrawText(g, rec ? "● Recording" : "Camera", FontHelper.Ui(11f, FontStyle.Bold),
                    new Rectangle(0, Circle / 2 + 6, Circle, 28), Color.White, TextFormatFlags.HorizontalCenter);
            };
            content.Controls.Add(_previewHost);

            _duration = new Label { Left = 0, Top = 312, Width = 360, Height = 26, ForeColor = fg, Font = FontHelper.Ui(13f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Text = "" };
            _hint = new Label { Left = 20, Top = 340, Width = 320, Height = 22, ForeColor = _dark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110), Font = FontHelper.Ui(9f), TextAlign = ContentAlignment.MiddleCenter, Text = "Preparing camera…" };
            content.Controls.Add(_duration); content.Controls.Add(_hint);

            _flipBtn = MakeBtn("Flip", 20, 376, 96);
            _flipBtn.Click += async (s, e) => { try { _hint.Text = "Switching camera…"; await _rec.SwitchCameraAsync(); _hint.Text = ""; } catch { } };
            _recordBtn = MakeBtn("● Record", 132, 376, 96, accent: true);
            _recordBtn.Click += OnRecordClick;
            _cancelBtn = MakeBtn("Cancel", 244, 376, 96);
            _cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            _sendBtn = MakeBtn("Send", 132, 376, 96, accent: true);
            _sendBtn.Click += OnSendClick;
            _sendBtn.Visible = false;
            content.Controls.Add(_flipBtn); content.Controls.Add(_recordBtn); content.Controls.Add(_sendBtn); content.Controls.Add(_cancelBtn);

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (s, e) =>
            {
                _seconds++;
                _duration.Text = TimeSpan.FromSeconds(_seconds).ToString(@"m\:ss");
                if (_seconds >= MaxSeconds) { var ignore = StopRecordingAsync(); }
            };
        }

        private Button MakeBtn(string text, int x, int y, int w, bool accent = false)
        {
            var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = 40, FlatStyle = FlatStyle.Flat, Font = FontHelper.Ui(10f, FontStyle.Bold), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = accent ? 0 : 1;
            b.BackColor = accent ? _accent : (_dark ? Color.FromArgb(54, 54, 58) : Color.White);
            b.ForeColor = accent ? Color.White : (_dark ? Color.White : Color.FromArgb(20, 20, 20));
            return b;
        }

        private async void OnShownAsync(object sender, EventArgs e)
        {
            try
            {
                _rec = await CameraCapture.CreateAsync();
                if (_rec == null) { _hint.Text = "No camera available on this device."; _recordBtn.Enabled = _flipBtn.Enabled = false; return; }
                _rec.Failed += ex => { try { BeginInvoke((Action)(() => _hint.Text = "Camera error: " + ex.Message)); } catch { } };
                _hasPreview = _rec.TryAttachPreview(_previewHost);
                _flipBtn.Visible = _rec.Cameras.Count > 1;
                _hint.Text = _hasPreview ? "Tap Record to start." : "Live preview unavailable — tap Record.";
                _state = St.Idle;
            }
            catch (Exception ex) { _hint.Text = "Camera init failed: " + ex.Message; System.Diagnostics.Debug.WriteLine("[ROUND-REC] init failed: " + ex.Message); }
        }

        private async void OnRecordClick(object sender, EventArgs e)
        {
            if (_state == St.Idle) await StartRecordingAsync();
            else if (_state == St.Recording) await StopRecordingAsync();
            else if (_state == St.Recorded) ResetForRetake();
        }

        private async Task StartRecordingAsync()
        {
            try
            {
                _tempPath = Path.Combine(AppSettings.Instance.MediaCacheFolder, "round_" + Guid.NewGuid().ToString("N") + ".mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(_tempPath));
                _state = St.Recording; _seconds = 0; _duration.Text = "0:00"; _hint.Text = "Recording… tap to stop.";
                _recordBtn.Text = "■ Stop"; _flipBtn.Enabled = false; _previewHost.Invalidate();
                System.Diagnostics.Debug.WriteLine("[ROUND-REC] record start");
                await _rec.StartAsync(_tempPath);
                _timer.Start();
            }
            catch (Exception ex) { _hint.Text = "Couldn't start: " + ex.Message; _state = St.Idle; _recordBtn.Text = "● Record"; _flipBtn.Enabled = true; }
        }

        private async Task StopRecordingAsync()
        {
            if (_state != St.Recording) return;
            _timer.Stop();
            _state = St.Recorded; _recordBtn.Enabled = false; _hint.Text = "Finalizing…";
            try
            {
                _recordedPath = await _rec.StopAsync();
                System.Diagnostics.Debug.WriteLine("[ROUND-REC] record stop duration=" + _seconds + "s → " + _recordedPath);

                // FINALIZATION CHECK: probe the file — a truncated MP4 (no moov) is unplayable even though it exists.
                var info = await VideoProbe.ProbeAsync(_recordedPath, CancellationToken.None);
                long bytes = 0; try { bytes = new FileInfo(_recordedPath).Length; } catch { }
                bool playable = info != null && info.DurationSec > 0 && bytes > 0;
                System.Diagnostics.Debug.WriteLine("[ROUND-REC] recording finalized, MP4 " + (playable ? "playable (moov present)" : "NOT playable") + ", " + bytes + " bytes, " + info.Width + "x" + info.Height);
                if (!playable) { _hint.Text = "Recording failed (incomplete file). Retake."; ShowRetake(); return; }

                _hint.Text = "Tap Send, or Retake."; ShowSendRetake();
            }
            catch (Exception ex) { _hint.Text = "Stop failed: " + ex.Message; ShowRetake(); }
            finally { _recordBtn.Enabled = true; _previewHost.Invalidate(); }
        }

        private void ShowSendRetake()
        {
            _recordBtn.Text = "↺ Retake"; _recordBtn.Visible = true;
            _sendBtn.Visible = true; _sendBtn.Enabled = true;
            _flipBtn.Visible = false;
            // record button on the left, send in the middle, cancel on the right
            _recordBtn.SetBounds(20, 376, 96, 40);
        }

        private void ShowRetake()
        {
            _state = St.Recorded;
            _recordBtn.Text = "↺ Retake"; _recordBtn.SetBounds(132, 376, 96, 40); _recordBtn.Visible = true;
            _sendBtn.Visible = false; _flipBtn.Visible = false;
        }

        private void ResetForRetake()
        {
            TryDeleteTemp();
            _state = St.Idle; _seconds = 0; _duration.Text = ""; _hint.Text = _hasPreview ? "Tap Record to start." : "Tap Record.";
            _recordBtn.Text = "● Record"; _recordBtn.SetBounds(132, 376, 96, 40);
            _sendBtn.Visible = false; _flipBtn.Visible = _rec != null && _rec.Cameras.Count > 1; _flipBtn.Enabled = true;
            _previewHost.Invalidate();
        }

        private async void OnSendClick(object sender, EventArgs e)
        {
            if (_sending || string.IsNullOrEmpty(_recordedPath)) return;
            _sending = true; _sendBtn.Enabled = false; _recordBtn.Enabled = false; _hint.Text = "Sending…";
            try
            {
                // Release the camera BEFORE uploading (don't hold it during the network send).
                try { _rec.Dispose(); _rec = null; } catch { }
                WTelegram.Client.ProgressCallback progress = (t, tot) =>
                {
                    if (tot <= 0) return; int pct = (int)(t * 100 / tot);
                    try { BeginInvoke((Action)(() => _hint.Text = "Sending… " + pct + "%")); } catch { }
                };
                _sentMessage = await MediaSender.SendRoundVideoAsync(_service.Client, _peer, _recordedPath, _seconds, progress, CancellationToken.None);
                System.Diagnostics.Debug.WriteLine("[ROUND-REC] sent round video msgId=" + (_sentMessage != null ? _sentMessage.ID : 0));
                _recordedPath = null;   // sent — keep the file? no, it's uploaded; cleanup deletes the temp
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { _hint.Text = "Send failed: " + ex.Message; _sending = false; _sendBtn.Enabled = true; _recordBtn.Enabled = true; }
        }

        private void Cleanup()
        {
            try { _timer.Stop(); } catch { }
            try { if (_rec != null) { _rec.Dispose(); _rec = null; } } catch { }   // releases the camera
            TryDeleteTemp();
            System.Diagnostics.Debug.WriteLine("[ROUND-REC] recorder closed, camera released");
        }

        private void TryDeleteTemp()
        {
            try { if (!string.IsNullOrEmpty(_recordedPath) && File.Exists(_recordedPath)) File.Delete(_recordedPath); } catch { }
            try { if (!string.IsNullOrEmpty(_tempPath) && _tempPath != _recordedPath && File.Exists(_tempPath)) File.Delete(_tempPath); } catch { }
        }
    }
}
