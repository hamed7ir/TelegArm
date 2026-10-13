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
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Telegram-style round-video recorder. Backend-BLIND: talks only to <see cref="ICameraRecorder"/> (WinRT
    /// MediaCapture by default, libVLC fallback) — no backend branches here. A circular live preview (or a
    /// recording indicator if the backend can't preview), tap-to-start/tap-to-stop record, Front/Back flip,
    /// a duration counter (≤60s), and Send/Retake/Cancel. The camera is released on close/cancel.
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply). The camera / recorder
    /// lifecycle is the 1.x one, statement for statement: create on Shown, start / stop with the same ordering, the camera
    /// released before the upload and again (idempotently) in FormClosed. Layout: the circle, the duration and the hint
    /// are one <see cref="RecorderStage"/> row in the dialog body; Flip / Record(Stop, Retake) / Send / Cancel are the
    /// footer, added so that the left-to-right order is the 1.x one (Flip, Record, Cancel; Retake, Send, Cancel once a
    /// clip exists) — the 1.x SetBounds juggling is the footer's flow now. The button labels lost their symbol glyphs
    /// (tofu on RT 8.1): "Record", "Stop", "Retake"; the placeholder's "Recording" keeps its drawn red dot.
    /// ⚠ EVERY exit goes through Close(): Cancel sets DialogResult THEN calls Close() (as in 1.x), and Esc is the Cancel
    /// button's own click (CancelButton) — a bare DialogResult would hide the dialog without FormClosed, and the camera
    /// would stay held.
    /// </summary>
    public sealed class RoundRecorderForm : DialogForm
    {
        private readonly TelegramService _service;
        private readonly InputPeer _peer;

        private ICameraRecorder _rec;
        private Panel _previewHost;
        private ThemedLabel _hint, _duration;
        private RoundedButton _recordBtn, _flipBtn, _sendBtn, _cancelBtn;
        private System.Windows.Forms.Timer _timer;
        private int _seconds;
        private const int MaxSeconds = 60;
        private const int Circle = 280;   // logical px

        private string _tempPath, _recordedPath;
        private bool _hasPreview, _sending;
        private TL.Message _sentMessage;
        /// <summary>The sent round-video Message (set on a successful send) so the caller can append it live.</summary>
        public TL.Message SentMessage { get { return _sentMessage; } }
        private enum St { Init, Idle, Recording, Recorded }
        private St _state = St.Init;

        public RoundRecorderForm(TelegramService service, InputPeer peer) : base("Round video", 360, 480)
        {
            _service = service; _peer = peer;
            BuildUi();
            Shown += OnShownAsync;
            FormClosed += (s, e) => Cleanup();
        }

        private void BuildUi()
        {
            _previewHost = new Panel { BackColor = Color.Black };   // the video's own surround; the stage places and sizes it
            _previewHost.SizeChanged += (s, e) => RoundPreview();
            _previewHost.Paint += PaintPlaceholder;   // shown until/unless a live preview is attached

            _duration = new ThemedLabel
            {
                Role = LabelRole.Title, PointSize = TextRoles.Points(TextRole.Title),
                TextAlign = HorizontalAlignment.Center, Text = ""
            };
            _hint = new ThemedLabel
            {
                Role = LabelRole.Dim, PointSize = TextRoles.Points(TextRole.Field),
                TextAlign = HorizontalAlignment.Center, Ellipsis = true, Text = "Preparing camera…"
            };
            PopulateBody(new RecorderStage(_previewHost, _duration, _hint));

            // Footer: the first added is the RIGHTMOST, so this order reads Flip | Record | Send | Cancel from the left.
            // Hidden buttons take no room, so the visible ones keep the 1.x order in each state.
            _cancelBtn = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.None, 96);
            _cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            _sendBtn = AddFooterButton("Send", RoundedButtonKind.Primary, DialogResult.None, 96);
            _sendBtn.Click += OnSendClick;
            _sendBtn.Visible = false;
            _recordBtn = AddFooterButton("Record", RoundedButtonKind.Primary, DialogResult.None, 96);
            _recordBtn.Click += OnRecordClick;
            _flipBtn = AddFooterButton("Flip", RoundedButtonKind.Secondary, DialogResult.None, 96);
            _flipBtn.Click += async (s, e) => { try { _hint.Text = "Switching camera…"; await _rec.SwitchCameraAsync(); _hint.Text = ""; } catch { } };
            _flipBtn.TabIndex = 0; _recordBtn.TabIndex = 1; _sendBtn.TabIndex = 2; _cancelBtn.TabIndex = 3;   // Tab runs left to right
            CancelButton = _cancelBtn;   // Esc = Cancel, through Close() (see the class notes)

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (s, e) =>
            {
                _seconds++;
                _duration.Text = TimeSpan.FromSeconds(_seconds).ToString(@"m\:ss");
                if (_seconds >= MaxSeconds) { var ignore = StopRecordingAsync(); }
            };

            FitToContent();
        }

        /// <summary>The preview is a circle: re-cut the host's window region whenever its size changes (first layout, a DPI
        /// change) — the 1.x code cut it once, at a fixed 280 px.</summary>
        private void RoundPreview()
        {
            int w = _previewHost.Width, h = _previewHost.Height;
            if (w < 2 || h < 2) return;
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(0, 0, w, h);
                _previewHost.Region = new Region(p);
            }
        }

        /// <summary>The disc shown while no live preview is attached: a camera dark in either theme, with a dot (red while
        /// recording) and a word. Painted only until/unless the backend attaches a preview.</summary>
        private void PaintPlaceholder(object sender, PaintEventArgs e)
        {
            if (_hasPreview) return;
            var p = ThemeHelper.Palette;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = _previewHost.Width, h = _previewHost.Height;
            using (var b = new SolidBrush(ColorMath.Mix(Color.Black, Color.White, 0.2f))) g.FillEllipse(b, 0, 0, w, h);
            bool rec = _state == St.Recording;
            int dot = Px(24);
            using (var rb = new SolidBrush(rec ? p.Danger : ColorMath.Mix(Color.Black, Color.White, 0.5f)))
                g.FillEllipse(rb, w / 2 - dot / 2, h / 2 - Px(28), dot, dot);
            string label = rec ? "Recording" : "Camera";
            int textTop = h / 2 + Px(6);
            TextRenderer.DrawText(g, label, TextRoles.Font(TextRole.Section, WindowDpiValue, label),
                Rectangle.FromLTRB(0, textTop, w, textTop + Px(28)), Color.White, TextFormatFlags.HorizontalCenter);
        }

        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();   // first called from DialogForm's constructor, before the controls exist
            if (_previewHost != null) _previewHost.Invalidate();   // the recording dot is the palette's Danger
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
            catch (Exception ex) { _hint.Text = "Camera init failed: " + ex.Message; Logger.Diag("[ROUND-REC] init failed: " + ex.Message); }
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
                _recordBtn.Text = "Stop"; _flipBtn.Enabled = false; _previewHost.Invalidate();
                Logger.Diag("[ROUND-REC] record start");
                await _rec.StartAsync(_tempPath);
                _timer.Start();
            }
            catch (Exception ex) { _hint.Text = "Couldn't start: " + ex.Message; _state = St.Idle; _recordBtn.Text = "Record"; _flipBtn.Enabled = true; }
        }

        private async Task StopRecordingAsync()
        {
            if (_state != St.Recording) return;
            _timer.Stop();
            _state = St.Recorded; _recordBtn.Enabled = false; _hint.Text = "Finalizing…";
            try
            {
                _recordedPath = await _rec.StopAsync();
                Logger.Diag("[ROUND-REC] record stop duration=" + _seconds + "s → " + _recordedPath);

                // FINALIZATION CHECK: probe the file — a truncated MP4 (no moov) is unplayable even though it exists.
                var info = await VideoProbe.ProbeAsync(_recordedPath, CancellationToken.None);
                long bytes = 0; try { bytes = new FileInfo(_recordedPath).Length; } catch { }
                bool playable = info != null && info.DurationSec > 0 && bytes > 0;
                // ⚠ Logger.Diag (unlike the Debug.WriteLine it replaces) evaluates its argument in Release too: info may be null.
                Logger.Diag("[ROUND-REC] recording finalized, MP4 " + (playable ? "playable (moov present)" : "NOT playable") + ", " + bytes + " bytes, " + (info != null ? info.Width + "x" + info.Height : "no probe"));
                if (!playable) { _hint.Text = "Recording failed (incomplete file). Retake."; ShowRetake(); return; }

                _hint.Text = "Tap Send, or Retake."; ShowSendRetake();
            }
            catch (Exception ex) { _hint.Text = "Stop failed: " + ex.Message; ShowRetake(); }
            finally { _recordBtn.Enabled = true; _previewHost.Invalidate(); }
        }

        private void ShowSendRetake()
        {
            // Retake on the left, Send in the middle, Cancel on the right — the footer's order; Send is the primary action now.
            _recordBtn.Text = "Retake"; _recordBtn.Kind = RoundedButtonKind.Secondary; _recordBtn.Visible = true;
            _sendBtn.Visible = true; _sendBtn.Enabled = true;
            _flipBtn.Visible = false;
        }

        private void ShowRetake()
        {
            _state = St.Recorded;
            _recordBtn.Text = "Retake"; _recordBtn.Kind = RoundedButtonKind.Primary; _recordBtn.Visible = true;
            _sendBtn.Visible = false; _flipBtn.Visible = false;
        }

        private void ResetForRetake()
        {
            TryDeleteTemp();
            _state = St.Idle; _seconds = 0; _duration.Text = ""; _hint.Text = _hasPreview ? "Tap Record to start." : "Tap Record.";
            _recordBtn.Text = "Record"; _recordBtn.Kind = RoundedButtonKind.Primary;
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
                Logger.Diag("[ROUND-REC] sent round video msgId=" + (_sentMessage != null ? _sentMessage.ID : 0));
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
            Logger.Diag("[ROUND-REC] recorder closed, camera released");
        }

        private void TryDeleteTemp()
        {
            try { if (!string.IsNullOrEmpty(_recordedPath) && File.Exists(_recordedPath)) File.Delete(_recordedPath); } catch { }
            try { if (!string.IsNullOrEmpty(_tempPath) && _tempPath != _recordedPath && File.Exists(_tempPath)) File.Delete(_tempPath); } catch { }
        }

        // ── the stage ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The one body row: the circular preview host centred at the top, the duration under it and the hint under
        /// that. It owns only the geometry (logical px at the window's DPI); the form owns what the children show.</summary>
        private sealed class RecorderStage : SettingRow
        {
            private const int CircleTop = 24, DurationTop = 312, DurationH = 26, HintTop = 340, HintH = 22, HintPad = 20, Side = 8, StageH = 372;
            private readonly Control _preview, _duration, _hint;

            public RecorderStage(Control preview, Control duration, Control hint) : base("", false)
            {
                _preview = preview; _duration = duration; _hint = hint;
                AccessibleRole = AccessibleRole.Grouping;
                AccessibleName = "Camera";
                Controls.Add(preview); Controls.Add(duration); Controls.Add(hint);
                Refit();
            }

            public override int HeightFor(int width, int dpi) { return Dpi.Px(StageH, dpi); }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (_preview == null) return;
                int dpi = Dpi.Of(this);
                int c = Math.Min(Dpi.Px(Circle, dpi), Math.Max(1, Width - 2 * Dpi.Px(Side, dpi)));   // never wider than the row
                _preview.SetBounds((Width - c) / 2, Dpi.Px(CircleTop, dpi), c, c);
                _duration.SetBounds(0, Dpi.Px(DurationTop, dpi), Width, Dpi.Px(DurationH, dpi));
                int pad = Dpi.Px(HintPad, dpi);
                _hint.SetBounds(pad, Dpi.Px(HintTop, dpi), Math.Max(1, Width - 2 * pad), Dpi.Px(HintH, dpi));
            }
        }
    }
}
