using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using TelegArm.Core;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Telegram-style audio/voice bubble: a circle button (download / play / pause /
    /// spinner) plus two text lines (title + "duration, size" or "elapsed / total").
    /// The waveform/seek/speed live in the persistent MiniPlayerBar, not here.
    /// </summary>
    public class VoiceBubbleControl : Control, IFlashable
    {
        private readonly Font _nameFont = FontHelper.Ui(9f, FontStyle.Bold);
        private readonly Font _subFont = FontHelper.Ui(8f);

        private readonly long _id;
        private readonly int _durationSec;
        private readonly bool _isAudio;
        private readonly string _title;
        private readonly string _sizeText;
        private readonly bool _outgoing;
        private readonly Func<DownloadHandle> _startDownload;
        private readonly System.Windows.Forms.Timer _timer;

        private string _path;
        private DownloadHandle _handle;   // in-flight download (ring/MB/cancel); null when idle/done
        private int _lastUiTick;          // throttle for progress repaints
        private Image _cover;             // optional embedded cover art (clipped into the button); null = plain circle.
                                          // NON-owning: lives in MainForm._photoThumbCache (chat-scoped) — do NOT dispose here.

        // Brief accent flash (used when a Show-in-chat / scroll-to-message jump lands on this bubble).
        private System.Windows.Forms.Timer _flashTimer;
        private int _flashTicks;

        public Color AccentColor { get; set; } = Color.DodgerBlue;
        public bool IsDark { get; set; }

        /// <summary>Telegram message id this bubble renders (for context-menu actions).</summary>
        public int MessageId { get; set; }

        /// <summary>Raised on right-click / touch-and-hold / menu key, with the screen anchor.</summary>
        public event EventHandler<Point> ContextMenuRequested;

        private const int WM_CONTEXTMENU = 0x007B;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CONTEXTMENU)
            {
                int lp = m.LParam.ToInt32();
                Point pt = lp == -1
                    ? PointToScreen(new Point(Width / 2, Height / 2))
                    : new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF)));
                ContextMenuRequested?.Invoke(this, pt);
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>When true, a left-click toggles selection instead of playing.</summary>
        public bool SelectionMode { get; set; }
        /// <summary>Whether this bubble is currently selected.</summary>
        public bool Selected { get; set; }
        /// <summary>Raised when clicked while in selection mode.</summary>
        public event EventHandler SelectionToggled;

        private const int CardW = 250, CardH = 56, VMargin = 4, SideGap = 12, Pad = 10, BtnD = 44;

        public VoiceBubbleControl(long id, int durationSec, bool isAudio, string fileName, string sizeText,
                                  bool outgoing, string cachedPath, Func<DownloadHandle> startDownload)
        {
            _id = id; _durationSec = durationSec; _isAudio = isAudio; _sizeText = sizeText; _outgoing = outgoing;
            _startDownload = startDownload;
            _path = cachedPath;
            _title = isAudio ? StripExt(fileName) : "Voice message";

            Margin = new Padding(0);
            Height = CardH + 2 * VMargin;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _timer = new System.Windows.Forms.Timer { Interval = 200 };
            _timer.Tick += (s, e) => Tick();
            AudioPlayer.StateChanged += OnAudioState;
        }

        /// <summary>Keeps the elapsed/total line live whenever this clip is the current one,
        /// even if playback was started/advanced from the mini player rather than this bubble.</summary>
        private void OnAudioState()
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (IsCurrent && !_timer.Enabled) _timer.Start();
                    Invalidate();
                }));
            }
            catch { }
        }

        /// <summary>Supplies the track's embedded cover art (drawn clipped into the button circle). Pass null /
        /// never call for voice messages or art-less tracks → the plain circle fallback. The bitmap is owned by
        /// MainForm's per-chat thumb cache (disposed on chat-switch), so this control never disposes it.</summary>
        public void SetCover(Image cover)
        {
            if (IsDisposed) return;
            _cover = cover;
            Invalidate();
        }

        private static string StripExt(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Audio";
            try { return Path.GetFileNameWithoutExtension(name); } catch { return name; }
        }

        private bool IsCurrent => AudioPlayer.CurrentId == _id;

        /// <summary>Briefly pulses an accent tint over the bubble (mirrors MessageBubbleControl.Flash).</summary>
        public void Flash()
        {
            _flashTicks = 6;
            if (_flashTimer == null)
            {
                _flashTimer = new System.Windows.Forms.Timer { Interval = 120 };
                _flashTimer.Tick += (s, e) => { if (--_flashTicks <= 0) _flashTimer.Stop(); if (!IsDisposed) Invalidate(); };
            }
            _flashTimer.Start();
            if (!IsDisposed) Invalidate();
        }

        /// <summary>The visible card rectangle (matches OnPaint). The control spans the full row width for
        /// alignment, so only this rect is interactive — taps in the dead space beside it do nothing.</summary>
        private Rectangle CardRect()
        {
            int bx = _outgoing ? Width - CardW - SideGap : SideGap;
            return new Rectangle(bx, VMargin, CardW, CardH);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (SelectionMode) { SelectionToggled?.Invoke(this, EventArgs.Empty); return; }
            if (!CardRect().Contains(e.Location))
            {
                if (TelegArm.Helpers.Logger.Enabled) Debug.WriteLine("[HITTEST] voice: tap outside card → ignored");
                return;   // dead space beside the bubble → inert
            }
            HandleCardTap(e.Location);
        }

        /// <summary>The play/download button circle (matches OnPaint) — also the ✕ cancel hit target while downloading.</summary>
        private Rectangle ButtonRect()
        {
            var card = CardRect();
            return new Rectangle(card.X + Pad, card.Y + (CardH - BtnD) / 2, BtnD, BtnD);
        }

        private void HandleCardTap(Point loc)
        {
            if (_handle != null && _handle.State == DownloadHandle.DState.Downloading)
            {
                // While downloading, the button center is the ✕ cancel; taps elsewhere on the card do nothing.
                if (ButtonRect().Contains(loc)) { Debug.WriteLine("[DL] cancel tap id=" + _id); _handle.Cancel(); }
                return;
            }
            if (!string.IsNullOrEmpty(_path) && File.Exists(_path)) { TogglePlay(); return; }
            StartDownload();
        }

        private void StartDownload()
        {
            if (_startDownload == null) return;
            var h = _startDownload();
            if (h == null) return;
            if (h.State == DownloadHandle.DState.Done) { _path = h.Path; Invalidate(); return; }   // already cached
            _handle = h;
            h.Changed += OnHandleChanged;
            Invalidate();
        }

        private void TogglePlay()
        {
            if (!AudioPlayer.Toggle(_id, _path, _title)) { try { Process.Start(_path); } catch { } return; }   // VLC unavailable → system player
            _timer.Start();
            Invalidate();
        }

        // Progress fires on the download thread → throttle + marshal to the UI thread.
        private void OnHandleChanged(DownloadHandle h)
        {
            if (IsDisposed) return;
            if (h.State == DownloadHandle.DState.Downloading)
            {
                int now = Environment.TickCount;
                if (now - _lastUiTick < 150) return;   // ~6-7 repaints/sec max (don't flood the UI thread on RT)
                _lastUiTick = now;
            }
            try { BeginInvoke((Action)(() => OnHandleChangedUi(h))); } catch { }
        }

        private void OnHandleChangedUi(DownloadHandle h)
        {
            if (IsDisposed) return;
            if (h.State != DownloadHandle.DState.Downloading)   // finished (done / cancelled / failed)
            {
                h.Changed -= OnHandleChanged;
                if (_handle == h) _handle = null;
                if (h.State == DownloadHandle.DState.Done) { _path = h.Path; TogglePlay(); }   // auto-play (user tapped to play)
                // Cancelled / Failed → _path stays null → back to "tap to download" (retryable)
            }
            Invalidate();
        }

        private void Tick()
        {
            // Drives the elapsed/total line while playing (download progress repaints come from OnHandleChanged).
            if (!IsCurrent || AudioPlayer.State == VLCState.Ended || AudioPlayer.State == VLCState.Stopped)
                _timer.Stop();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color baseBg = IsDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);
            g.Clear((SelectionMode && Selected) ? Blend(AccentColor, baseBg, 0.18f) : baseBg);

            int bx = _outgoing ? Width - CardW - SideGap : SideGap;
            int by = VMargin;
            var card = new Rectangle(bx, by, CardW, CardH);

            Color bubble = _outgoing ? AccentColor : (IsDark ? Color.FromArgb(60, 60, 63) : Color.FromArgb(226, 226, 226));
            Color fg = _outgoing ? Color.White : (IsDark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20));
            Color sub = _outgoing ? Color.FromArgb(210, 255, 255, 255) : (IsDark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(110, 110, 110));

            using (var b = new SolidBrush(bubble))
            using (var path = DrawHelper.RoundedRect(card, 12))
                g.FillPath(b, path);

            var btn = new Rectangle(bx + Pad, by + (CardH - BtnD) / 2, BtnD, BtnD);
            if (_cover != null)
                DrawHelper.DrawAudioCover(g, btn, _cover);   // cover art clipped into the circle (shared with album rows)
            else
                using (var cb = new SolidBrush(_outgoing ? Color.FromArgb(70, 255, 255, 255) : AccentColor))
                    g.FillEllipse(cb, btn);   // plain circle fallback (voice / no embedded art) — unchanged
            DrawButtonGlyph(g, btn);

            int tx = btn.Right + 12;
            int tw = bx + CardW - Pad - tx;
            TextRenderer.DrawText(g, _title, _nameFont, new Rectangle(tx, by + 9, tw, 20), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            string line2;
            if (_handle != null && _handle.State == DownloadHandle.DState.Downloading)
            {
                line2 = DrawHelper.FormatSize(_handle.Transmitted) + " / " + DrawHelper.FormatSize(_handle.Total);   // X.X / Y.Y MB
            }
            else if (IsCurrent)
            {
                int total = _durationSec > 0 ? _durationSec : (int)(AudioPlayer.DurationMs / 1000);
                line2 = Fmt((int)(AudioPlayer.TimeMs / 1000)) + " / " + Fmt(total);
            }
            else
            {
                line2 = Fmt(_durationSec);
                if (_isAudio && !string.IsNullOrEmpty(_sizeText)) line2 += ", " + _sizeText;
            }
            TextRenderer.DrawText(g, line2, _subFont, new Rectangle(tx, by + 30, tw, 18), sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            DrawSelectionCheck(g);
        }

        private void DrawSelectionCheck(Graphics g)
        {
            if (_flashTicks > 0)
                using (var fb = new SolidBrush(Color.FromArgb(70, AccentColor)))
                    g.FillRectangle(fb, ClientRectangle);   // brief "jump to message" pulse
            if (!SelectionMode) return;
            const int d = 18;
            int x = _outgoing ? SideGap + 2 : Width - SideGap - d - 2;
            int y = (Height - d) / 2;
            var circle = new Rectangle(x, y, d, d);
            if (Selected)
            {
                using (var b = new SolidBrush(AccentColor)) g.FillEllipse(b, circle);
                using (var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, new[]
                    {
                        new PointF(x + 4.5f, y + 9f), new PointF(x + 7.5f, y + 12f), new PointF(x + 13.5f, y + 6f)
                    });
            }
            else
            {
                using (var pen = new Pen(IsDark ? Color.FromArgb(120, 120, 120) : Color.FromArgb(170, 170, 170), 1.6f))
                    g.DrawEllipse(pen, circle);
            }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R * t + b.R * (1 - t)),
                (int)(a.G * t + b.G * (1 - t)),
                (int)(a.B * t + b.B * (1 - t)));
        }

        private void DrawButtonGlyph(Graphics g, Rectangle btn)
        {
            float cx = btn.X + BtnD / 2f, cy = btn.Y + BtnD / 2f;

            if (_handle != null && _handle.State == DownloadHandle.DState.Downloading)
            {
                // Real progress ring filling to % + an ✕ in the center (tap = cancel).
                DrawHelper.DrawProgressRing(g, Rectangle.Inflate(btn, -5, -5), _handle.Fraction,
                    Color.White, Color.FromArgb(120, 255, 255, 255), cancel: true);
                return;
            }

            using (var wb = new SolidBrush(Color.White))
            using (var pen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
                {
                    // download arrow
                    g.DrawLine(pen, cx, cy - 8, cx, cy + 5);
                    g.DrawLine(pen, cx - 6, cy - 1, cx, cy + 5);
                    g.DrawLine(pen, cx + 6, cy - 1, cx, cy + 5);
                    g.DrawLine(pen, cx - 7, cy + 9, cx + 7, cy + 9);
                }
                else if (AudioPlayer.IsPlayingId(_id))
                {
                    g.FillRectangle(wb, cx - 6, cy - 8, 4.5f, 16);
                    g.FillRectangle(wb, cx + 1.5f, cy - 8, 4.5f, 16);
                }
                else
                {
                    g.FillPolygon(wb, new[] { new PointF(cx - 6, cy - 9), new PointF(cx + 9, cy), new PointF(cx - 6, cy + 9) });
                }
            }
        }

        private static string Fmt(int s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return t.Hours > 0 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Cancel any in-flight download so it can't outlive the bubble (zombie transfer).
                if (_handle != null) { _handle.Changed -= OnHandleChanged; try { _handle.Cancel(); } catch { } _handle = null; }
                AudioPlayer.StateChanged -= OnAudioState;
                _timer?.Stop();
                _timer?.Dispose();
                _flashTimer?.Dispose();
                _nameFont.Dispose();
                _subFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
