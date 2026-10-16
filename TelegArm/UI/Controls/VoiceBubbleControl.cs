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
    ///
    /// Redesign step 3.2: every length is logical (96-DPI) through Px at the row's DPI; fonts are shared FontCache fonts
    /// (never disposed); the glyphs scale from the rectangle they are drawn in. The height is SET from the logical value
    /// on parenting and on a DPI change, never multiplied.
    /// </summary>
    public class VoiceBubbleControl : Control, IFlashable, IDpiAware
    {
        private int _dpi;                                          // the DPI this row is laid out at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

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

        private const int CardW = 250, CardH = 56, VMargin = 4, SideGap = 12, Pad = 10, BtnD = 44;   // logical px

        public VoiceBubbleControl(long id, int durationSec, bool isAudio, string fileName, string sizeText,
                                  bool outgoing, string cachedPath, Func<DownloadHandle> startDownload)
        {
            _id = id; _durationSec = durationSec; _isAudio = isAudio; _sizeText = sizeText; _outgoing = outgoing;
            _startDownload = startDownload;
            _path = cachedPath;
            _title = isAudio ? StripExt(fileName) : "Voice message";

            Margin = new Padding(0);
            Height = RowHeight(CurDpi);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            _timer = new System.Windows.Forms.Timer { Interval = 200 };
            _timer.Tick += (s, e) => Tick();
            AudioPlayer.StateChanged += OnAudioState;
        }

        /// <summary>The card plus its top and bottom margin, rounded per term exactly as OnPaint places them.</summary>
        private static int RowHeight(int dpi) { return Dpi.Px(CardH, dpi) + 2 * Dpi.Px(VMargin, dpi); }

        /// <summary>Rows are built before they are added: the window's DPI is known only once parented.</summary>
        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d != _dpi) { _dpi = d; Height = RowHeight(d); Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi)
        {
            _dpi = newDpi;
            Height = RowHeight(newDpi);   // SET from the logical value: DpiScaler already ratio-scaled the bounds
            Invalidate();
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
        private Rectangle CardRect() { return CardRect(CurDpi); }

        private Rectangle CardRect(int dpi)
        {
            int cardW = Dpi.Px(CardW, dpi), sideGap = Dpi.Px(SideGap, dpi);
            int bx = _outgoing ? Width - cardW - sideGap : sideGap;
            return new Rectangle(bx, Dpi.Px(VMargin, dpi), cardW, Dpi.Px(CardH, dpi));
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
        private Rectangle ButtonRect() { return ButtonRect(CardRect(CurDpi), CurDpi); }

        private static Rectangle ButtonRect(Rectangle card, int dpi)
        {
            int btnD = Dpi.Px(BtnD, dpi);
            return new Rectangle(card.X + Dpi.Px(Pad, dpi), card.Y + (card.Height - btnD) / 2, btnD, btnD);
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
            int dpi = CurDpi;
            int P(int v) => Dpi.Px(v, dpi);
            var nameFont = FontHelper.UiAt(9f, dpi, FontStyle.Bold);   // shared: never dispose
            var subFont = FontHelper.UiAt(8f, dpi);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color baseBg = ThemeHelper.PaletteFor(IsDark).ChatBg;
            ChatBackdrop.PaintRow(g, this, e.ClipRectangle, IsDark, (SelectionMode && Selected) ? Blend(AccentColor, baseBg, 0.18f) : baseBg,
                                  (SelectionMode && Selected) ? AccentColor : (Color?)null);   // step 5.4

            var card = CardRect(dpi);
            int bx = card.X, by = card.Y;

            Color bubble = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOut : (ThemeHelper.PaletteFor(IsDark).BubbleIn);
            Color fg = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutText : (ThemeHelper.PaletteFor(IsDark).BubbleInText);
            Color sub = _outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutSub : (ThemeHelper.PaletteFor(IsDark).BubbleInSub);

            using (var b = new SolidBrush(bubble))
            using (var path = DrawHelper.RoundedRect(card, P(12)))
                g.FillPath(b, path);

            var btn = ButtonRect(card, dpi);
            if (_cover != null)
                DrawHelper.DrawAudioCover(g, btn, _cover);   // cover art clipped into the circle (shared with album rows)
            else
                using (var cb = new SolidBrush(_outgoing ? ThemeHelper.PaletteFor(IsDark).BubbleOutIconDisc : AccentColor))
                    g.FillEllipse(cb, btn);   // plain circle fallback (voice / no embedded art) — unchanged
            DrawButtonGlyph(g, btn, dpi);

            int tx = btn.Right + P(12);
            int tw = bx + card.Width - P(Pad) - tx;
            TextRenderer.DrawText(g, _title, nameFont, new Rectangle(tx, by + P(9), tw, P(20)), fg,
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
            TextRenderer.DrawText(g, line2, subFont, new Rectangle(tx, by + P(30), tw, P(18)), sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            DrawSelectionCheck(g, dpi);
        }

        private void DrawSelectionCheck(Graphics g, int dpi)
        {
            if (_flashTicks > 0)
                using (var fb = new SolidBrush(ColorMath.WithAlpha(AccentColor, 70)))
                    g.FillRectangle(fb, ClientRectangle);   // brief "jump to message" pulse
            if (!SelectionMode) return;
            int d = Dpi.Px(18, dpi), sideGap = Dpi.Px(SideGap, dpi), two = Dpi.Px(2, dpi);
            float k = d / 18f;   // the tick is drawn for an 18 px circle; scales with it
            int x = _outgoing ? sideGap + two : Width - sideGap - d - two;
            int y = (Height - d) / 2;
            var circle = new Rectangle(x, y, d, d);
            if (Selected)
            {
                using (var b = new SolidBrush(AccentColor)) g.FillEllipse(b, circle);
                using (var pen = new Pen(ThemeHelper.PaletteFor(IsDark).OnAccent, 2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, new[]
                    {
                        new PointF(x + 4.5f * k, y + 9f * k), new PointF(x + 7.5f * k, y + 12f * k), new PointF(x + 13.5f * k, y + 6f * k)
                    });
            }
            else
            {
                using (var pen = new Pen(ThemeHelper.PaletteFor(IsDark).BubbleSelectRing, 1.6f * k))
                    g.DrawEllipse(pen, circle);
            }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return ColorMath.Rgb(
                (int)(a.R * t + b.R * (1 - t)),
                (int)(a.G * t + b.G * (1 - t)),
                (int)(a.B * t + b.B * (1 - t)));
        }

        /// <summary>Drawn for the 44 px button; scales with the rectangle it is given.</summary>
        private void DrawButtonGlyph(Graphics g, Rectangle btn, int dpi)
        {
            float k = btn.Width / (float)BtnD;
            float cx = btn.X + btn.Width / 2f, cy = btn.Y + btn.Height / 2f;

            if (_handle != null && _handle.State == DownloadHandle.DState.Downloading)
            {
                // Real progress ring filling to % + an ✕ in the center (tap = cancel).
                int inset = Dpi.Px(5, dpi);
                DrawHelper.DrawProgressRing(g, Rectangle.Inflate(btn, -inset, -inset), _handle.Fraction,
                    (_outgoing ? ThemeHelper.PaletteFor(IsDark).OnBubbleOut : ThemeHelper.PaletteFor(IsDark).OnAccent), ThemeHelper.PaletteFor(IsDark).MediaRingTrack, cancel: true);
                return;
            }

            using (var wb = new SolidBrush((_outgoing ? ThemeHelper.PaletteFor(IsDark).OnBubbleOut : ThemeHelper.PaletteFor(IsDark).OnAccent)))
            using (var pen = new Pen((_outgoing ? ThemeHelper.PaletteFor(IsDark).OnBubbleOut : ThemeHelper.PaletteFor(IsDark).OnAccent), 2.4f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
                {
                    // download arrow
                    g.DrawLine(pen, cx, cy - 8 * k, cx, cy + 5 * k);
                    g.DrawLine(pen, cx - 6 * k, cy - 1 * k, cx, cy + 5 * k);
                    g.DrawLine(pen, cx + 6 * k, cy - 1 * k, cx, cy + 5 * k);
                    g.DrawLine(pen, cx - 7 * k, cy + 9 * k, cx + 7 * k, cy + 9 * k);
                }
                else if (AudioPlayer.IsPlayingId(_id))
                {
                    g.FillRectangle(wb, cx - 6 * k, cy - 8 * k, 4.5f * k, 16 * k);
                    g.FillRectangle(wb, cx + 1.5f * k, cy - 8 * k, 4.5f * k, 16 * k);
                }
                else
                {
                    g.FillPolygon(wb, new[] { new PointF(cx - 6 * k, cy - 9 * k), new PointF(cx + 9 * k, cy), new PointF(cx - 6 * k, cy + 9 * k) });
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
                // (No font disposal: step 3.2 — the fonts are shared FontCache fonts, never disposed.)
            }
            base.Dispose(disposing);
        }
    }
}
