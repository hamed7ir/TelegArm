using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Core.Calls;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// The voice chat window (2026-10-16, Telegram group calls via tgcalls' group engine): the chat, what is happening
    /// (joining, connecting, the running time and the count) and who is there - us first, then whoever speaks (a ring that
    /// follows their level), raised hands, the rest - with Unmute / Mute, Raise hand when an admin muted us, Leave (an admin
    /// may end it for everyone), Close at the end (it also closes by itself). Not modal and on top, as CallWindow; it
    /// follows GroupCallManager.Changed / LevelsChanged. Telegram Desktop's panel is 380 x 520.
    /// </summary>
    public sealed class GroupCallWindow : DialogForm
    {
        private const int W = 380, H = 520;   // logical

        private GroupCallManager _calls;
        private GroupCallSession _s;
        private bool _anySpeaking;   // the last levels repaint showed someone speaking
        private readonly Func<long, Image> _avatar;
        private readonly GroupStage _stage;
        private readonly RoundedButton _mute, _hand, _leave, _close;
        private readonly Timer _tick;
        private int _endedTick;

        public GroupCallWindow(GroupCallManager calls, GroupCallSession s, Func<long, Image> avatar)
            : base("Voice chat", W, H)
        {
            _calls = calls; _s = s; _avatar = avatar;
            _stage = new GroupStage(this) { Dock = DockStyle.Fill };
            UseOwnBody(_stage);
            // first added = rightmost
            _leave = AddFooterButton("Leave", RoundedButtonKind.Danger, DialogResult.None, 100);
            _mute = AddFooterButton("Unmute", RoundedButtonKind.Primary, DialogResult.None, 100);
            _hand = AddFooterButton("Raise hand", RoundedButtonKind.Primary, DialogResult.None, 110);
            _close = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.None, 100);
            _mute.Click += (o, e) => { if (_s != null) _calls.SetMuted(!_s.Muted); };
            _hand.Click += async (o, e) => { if (_s != null) await _calls.RaiseHandAsync(!_s.HandRaised); };
            _leave.Click += async (o, e) => await LeaveAsync();
            _close.Click += (o, e) => Close();
            _calls.Changed += OnChanged;
            _calls.LevelsChanged += OnLevels;
            _tick = new Timer { Interval = 500 };
            _tick.Tick += (o, e) => Tick();
            _tick.Start();
            TopMost = true;
            Apply();
        }

        /// <summary>The voice chat this window shows.</summary>
        public GroupCallSession Session { get { return _s; } }

        /// <summary>Points the window at another voice chat (a new one while this window still shows the last one ending),
        /// of this or another account's manager.</summary>
        public void Bind(GroupCallManager calls, GroupCallSession s)
        {
            if (!ReferenceEquals(calls, _calls))
            {
                _calls.Changed -= OnChanged; _calls.LevelsChanged -= OnLevels;
                _calls = calls;
                _calls.Changed += OnChanged; _calls.LevelsChanged += OnLevels;
            }
            _s = s; _endedTick = 0; _stage.ScrollTo(0); Apply();
        }

        private void OnChanged(GroupCallSession s) { if (ReferenceEquals(s, _s) && !IsDisposed) Apply(); }

        /// <summary>Repaints for the levels only while someone speaks (or just stopped): tgcalls reports every 100 ms even in
        /// silence, and the list is not worth redrawing ten times a second on the Surface for nothing (review 2026-10-16).</summary>
        private void OnLevels(GroupCallSession s)
        {
            if (!ReferenceEquals(s, _s) || IsDisposed) return;
            bool any = s.MeSpeaking;
            if (!any) foreach (var p in s.Participants.All) if (p.Speaking) { any = true; break; }
            if (any || _anySpeaking) _stage.Invalidate();
            _anySpeaking = any;
        }

        private async System.Threading.Tasks.Task LeaveAsync()
        {
            var s = _s;
            if (s == null || s.Ended) return;
            if (s.CanManage)
            {
                int pick = ThemedDialog.Show(this, "Leave voice chat", "Leave the voice chat, or end it for everyone?", "Leave", "End for everyone", "Cancel");
                if (pick == 1) { await _calls.EndForAllAsync(); return; }
                if (pick != 0) return;
            }
            await _calls.LeaveAsync();
        }

        private void Apply()
        {
            var s = _s;
            if (s == null) return;
            Text = "Voice chat";   // the chat is named in the body
            bool ended = s.Ended;
            _close.Visible = ended;
            _leave.Visible = !ended;
            _mute.Visible = !ended && !s.ForceMuted;
            _hand.Visible = !ended && s.ForceMuted;
            _mute.Text = s.Muted ? "Unmute" : "Mute";
            _mute.Kind = s.Muted ? RoundedButtonKind.Primary : RoundedButtonKind.Secondary;
            _hand.Text = s.HandRaised ? "Lower hand" : "Raise hand";
            if (ended && _endedTick == 0) _endedTick = Environment.TickCount;
            _stage.Invalidate();
        }

        private void Tick()
        {
            _stage.Invalidate();   // the running time; who stopped speaking
            if (_endedTick != 0 && unchecked(Environment.TickCount - _endedTick) > 3000) { _endedTick = 0; Close(); }
        }

        internal string StatusText()
        {
            var s = _s;
            if (s == null) return "";
            int n = Math.Max(s.Participants.ServerCount, s.Participants.Count);
            string count = n == 1 ? "1 participant" : n + " participants";
            switch (s.Phase)
            {
                case GroupCallPhase.Joining: return "Joining…";
                case GroupCallPhase.Connecting: return "Connecting…  ·  " + count;
                case GroupCallPhase.Connected:
                    int d = s.DurationSeconds;
                    return (d >= 3600 ? (d / 3600) + ":" + (d / 60 % 60).ToString("00") : (d / 60).ToString()) + ":" + (d % 60).ToString("00") + "  ·  " + count;
                default: return s.EndText ?? "The voice chat has ended";
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // closing the window during the voice chat leaves it (as CallWindow hangs up)
            if (_s != null && !_s.Ended && e.CloseReason == CloseReason.UserClosing) { var t = _calls.LeaveAsync(); }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _calls.Changed -= OnChanged;
                _calls.LevelsChanged -= OnLevels;
                _tick.Dispose();
                foreach (var p in _icons.Values) p.Dispose();
                _icons.Clear();
            }
            base.Dispose(disposing);
        }

        // ── icons: Google's Material Design outlines (Apache-2.0; THIRD-PARTY-NOTICES), 24 x 24 viewBox ──────────────────

        internal const string MicPath =
            "M12 14c1.66 0 2.99-1.34 2.99-3L15 5c0-1.66-1.34-3-3-3S9 3.34 9 5v6c0 1.66 1.34 3 3 3z" +
            "M17.3 11c0 3-2.54 5.1-5.3 5.1S6.7 14 6.7 11H5c0 3.41 2.72 6.23 6 6.72V21h2v-3.28c3.28-.48 6-3.3 6-6.72h-1.7z";
        internal const string MicOffPath =
            "M19 11h-1.7c0 .74-.16 1.43-.43 2.05l1.23 1.23c.56-.98.9-2.09.9-3.28z" +
            "M14.98 11.17c0-.06.02-.11.02-.17V5c0-1.66-1.34-3-3-3S9 3.34 9 5v.18l5.98 5.99z" +
            "M4.27 3L3 4.27l6.01 6.01V11c0 1.66 1.33 3 2.99 3 .22 0 .44-.03.65-.08l1.66 1.66c-.71.33-1.5.52-2.31.52-2.76 0-5.3-2.1-5.3-5.1H5" +
            "c0 3.41 2.72 6.23 6 6.72V21h2v-3.28c.91-.13 1.77-.45 2.54-.9L19.73 21 21 19.73 4.27 3z";
        internal const string HandPath =
            "M23 5.5V20c0 2.2-1.8 4-4 4h-7.3c-1.08 0-2.1-.43-2.85-1.19L1 14.83s1.26-1.23 1.3-1.25c.22-.19.49-.29.79-.29.22 0 .42.06.6.16" +
            ".04.01 4.31 2.46 4.31 2.46V4c0-.83.67-1.5 1.5-1.5S11 3.17 11 4v7h1V1.5c0-.83.67-1.5 1.5-1.5S15 .67 15 1.5V11h1V2.5" +
            "c0-.83.67-1.5 1.5-1.5s1.5.67 1.5 1.5V11h1V5.5c0-.83.67-1.5 1.5-1.5s1.5.67 1.5 1.5z";
        /// <summary>record_voice_over: the header's "voice chat" button (its circle written as four curves: SvgPath has no arcs).</summary>
        internal const string VoiceChatPath =
            "M9 5c2.21 0 4 1.79 4 4s-1.79 4-4 4-4-1.79-4-4 1.79-4 4-4z" +
            "M9 15c-2.67 0-8 1.34-8 4v2h16v-2c0-2.66-5.33-4-8-4z" +
            "M16.76 5.36l-1.68 1.69c.84 1.18.84 2.71 0 3.89l1.68 1.69c2.02-2.02 2.02-5.07 0-7.27z" +
            "M20.07 2l-1.63 1.63c2.77 3.02 2.77 7.56 0 10.74L20.07 16c3.9-3.89 3.91-9.95 0-14z";

        internal static void FillIcon(Graphics g, string path, RectangleF box, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = SvgPath.Parse(path, 24f, box))
            using (var b = new SolidBrush(color))
                g.FillPath(b, p);
        }

        // The list's icons, parsed once per size (at the origin) and moved into place: no re-parse per row per paint.
        private readonly System.Collections.Generic.Dictionary<string, GraphicsPath> _icons = new System.Collections.Generic.Dictionary<string, GraphicsPath>();

        private void FillListIcon(Graphics g, string path, RectangleF box, Color color)
        {
            string key = path.Length + ":" + path.GetHashCode() + ":" + box.Width.ToString("0.0") + "x" + box.Height.ToString("0.0");
            GraphicsPath p;
            if (!_icons.TryGetValue(key, out p)) { p = SvgPath.Parse(path, 24f, new RectangleF(0, 0, box.Width, box.Height)); _icons[key] = p; }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var state = g.Save();
            try
            {
                g.TranslateTransform(box.X, box.Y);
                using (var b = new SolidBrush(color)) g.FillPath(b, p);
            }
            finally { g.Restore(state); }
        }

        /// <summary>One line of a name, title or bio: colour emoji through EmojiRenderer (astral glyphs are boxes in RT 8.1's
        /// GDI fonts), the rest through TextRenderer; ellipsis; right-aligned when it reads right to left.</summary>
        private static void DrawLine(Graphics g, string text, Font font, Color ink, Rectangle r)
        {
            if (string.IsNullOrEmpty(text) || r.Width < 1 || r.Height < 1) return;
            text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
            bool rtl = TextDirection.FirstStrongIsRtl(text);
            if (EmojiRenderer.ContainsEmoji(text))
            {
                try
                {
                    var old = g.TextRenderingHint;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    int x = r.X;
                    if (rtl) { int w = EmojiRenderer.MeasureLineWidth(g, text, font); if (w < r.Width) x = r.Right - w; }
                    EmojiRenderer.DrawLine(g, text, font, ink, new Rectangle(x, r.Y, r.Right - x, r.Height));
                    g.TextRenderingHint = old;
                    return;
                }
                catch { }
            }
            TextRenderer.DrawText(g, text, font, r, ink, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine
                                  | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
        }

        /// <summary>The chat and status on top, then the participants (wheel-scrolled, a drawn thumb: no native scroll bar).</summary>
        private sealed class GroupStage : Control
        {
            private readonly GroupCallWindow _w;
            private int _scroll, _contentH, _listTop, _hover = -1;
            private int _dragY = -1, _dragScroll;
            private bool _dragging;

            public GroupStage(GroupCallWindow w)
            {
                TabStop = false;   // R6: no key handling here, so not a Tab stop (TabStop only: Selectable stays, for the touch keyboard)
                _w = w;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            }

            public void ScrollTo(int y) { _scroll = Math.Max(0, Math.Min(y, Math.Max(0, _contentH - (Height - _listTop)))); Invalidate(); }

            // the wheel needs focus, but only within the voice chat window when it is already active: never take the keyboard
            // from the main window's message box just because the pointer crossed the list (review 2026-10-16)
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (ReferenceEquals(Form.ActiveForm, _w)) Focus(); }

            // a finger (or the mouse) drags the list: touch arrives as mouse messages on the Surface
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left && e.Y >= _listTop) { _dragY = e.Y; _dragScroll = _scroll; _dragging = false; }
            }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragY = -1; _dragging = false; }
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                ScrollTo(_scroll - e.Delta * Dpi.Px(56, _w.WindowDpiValue) / 120);
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_dragY >= 0 && (e.Button & MouseButtons.Left) != 0)
                {
                    if (!_dragging && Math.Abs(e.Y - _dragY) > Dpi.Px(8, _w.WindowDpiValue)) _dragging = true;
                    if (_dragging) { ScrollTo(_dragScroll - (e.Y - _dragY)); if (_hover != -1) { _hover = -1; } return; }
                }
                int row = e.Y < _listTop ? -1 : (e.Y - _listTop + _scroll) / Math.Max(1, Dpi.Px(56, _w.WindowDpiValue));
                if (row != _hover) { _hover = row; Invalidate(); }
            }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _dragY = -1; _dragging = false; if (_hover != -1) { _hover = -1; Invalidate(); } }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var p = ThemeHelper.Palette;
                int dpi = _w.WindowDpiValue;
                Func<int, int> Px = v => Dpi.Px(v, dpi);   // the window's DPI (the check script reads Px( as scaled)
                g.Clear(p.Window);
                var s = _w._s;
                if (s == null) return;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // the chat and what is happening
                string title = string.IsNullOrEmpty(s.Title) ? "Voice chat" : s.Title;
                var titleRect = new Rectangle(Px(16), Px(10), Width - Px(32), Px(26));
                DrawLine(g, title, FontHelper.ForAt(title, 13f, dpi, FontStyle.Bold), p.Text, titleRect);
                var stRect = new Rectangle(Px(16), titleRect.Bottom, Width - Px(32), Px(20));
                TextRenderer.DrawText(g, _w.StatusText(), FontHelper.UiAt(9.5f, dpi, FontStyle.Regular), stRect, s.Ended ? p.TextDim : p.Accent,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                int y = stRect.Bottom + Px(8);
                if (!string.IsNullOrEmpty(s.Note) && !s.Ended)
                {
                    var nr = new Rectangle(Px(16), y, Width - Px(32), Px(20));
                    TextRenderer.DrawText(g, s.Note, FontHelper.UiAt(9.5f, dpi, FontStyle.Regular), nr, p.TextDim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    y = nr.Bottom + Px(6);
                }
                using (var pen = new Pen(p.Divider)) g.DrawLine(pen, 0, y, Width, y);
                _listTop = y + 1;

                // who is there
                List<GroupParticipant> list = s.Participants.Sorted();
                int rowH = Px(56);
                _contentH = list.Count * rowH;
                int max = Math.Max(0, _contentH - (Height - _listTop));
                if (_scroll > max) _scroll = max;
                var clip = new Rectangle(0, _listTop, Width, Height - _listTop);
                g.SetClip(clip);
                int first = _scroll / rowH;
                for (int i = first; i < list.Count; i++)
                {
                    int top = _listTop + i * rowH - _scroll;
                    if (top > Height) break;
                    DrawRow(g, list[i], s, new Rectangle(0, top, Width, rowH), i == _hover, p, Px, dpi);
                }
                if (list.Count == 0 && !s.Ended)
                    TextRenderer.DrawText(g, s.Phase == GroupCallPhase.Joining ? "Loading…" : "No one is here yet", FontHelper.UiAt(10f, dpi, FontStyle.Regular),
                        new Rectangle(0, _listTop + Px(24), Width, Px(24)), p.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                if (max > 0)   // the drawn thumb
                {
                    int track = Height - _listTop, th = Math.Max(Px(24), track * track / _contentH);
                    int ty = _listTop + (track - th) * _scroll / max;
                    using (var b = new SolidBrush(p.ScrollThumb)) g.FillRectangle(b, Width - Px(5), ty, Px(3), th);
                }
                g.ResetClip();
            }

            private void DrawRow(Graphics g, GroupParticipant x, GroupCallSession s, Rectangle r, bool hover, Palette p, Func<int, int> Px, int dpi)
            {
                if (hover) using (var b = new SolidBrush(p.Hover)) g.FillRectangle(b, r);
                bool speaking = x.IsSelf ? s.MeSpeaking : x.Speaking;
                int d = Px(40);
                var ar = new Rectangle(r.X + Px(16), r.Y + (r.Height - d) / 2, d, d);
                Image av = _w._avatar != null ? _w._avatar(x.PeerId) : null;
                if (av != null) DrawHelper.DrawCircularImage(g, ar, av);
                else
                {
                    using (var b = new SolidBrush(DrawHelper.AvatarColor(x.PeerId))) g.FillEllipse(b, ar);
                    string ini = DrawHelper.Initial(x.Name ?? "?");
                    DrawHelper.DrawInitial(g, ini, FontHelper.ForAt(ini, 13f, dpi, FontStyle.Bold), ar, Color.White);
                }
                if (speaking)   // a ring that swells with the level
                {
                    float level = Math.Min(1f, x.IsSelf ? s.MyLevel : x.Level);
                    using (var pen = new Pen(p.Success, Math.Max(2f, Px(2) + level * Px(2))))
                        g.DrawEllipse(pen, Rectangle.Inflate(ar, Px(3), Px(3)));
                }
                int textX = ar.Right + Px(12), iconW = Px(22), textW = r.Right - textX - iconW - Px(20);
                string name = (x.Name ?? "Participant") + (x.IsSelf ? " (you)" : "");
                DrawLine(g, name, FontHelper.ForAt(name, 10.5f, dpi, FontStyle.Bold), p.Text, new Rectangle(textX, r.Y + Px(9), textW, Px(20)));
                string state;
                Color stateColor = p.TextDim;
                bool muted = x.IsSelf ? s.Muted : x.Muted;
                bool forced = x.IsSelf ? s.ForceMuted : x.Muted && !x.CanSelfUnmute;
                if (speaking) { state = "speaking"; stateColor = p.Success; }
                else if (x.IsSelf ? s.HandRaised : x.HandRaised) { state = "wants to speak"; stateColor = p.Accent; }
                else if (x.MutedByYou) state = "muted for you";
                else if (forced) state = "muted by admin";
                else if (!string.IsNullOrEmpty(x.About) && !x.IsSelf) state = x.About;
                else state = muted ? "listening" : "can speak";
                DrawLine(g, state, FontHelper.ForAt(state, 9.25f, dpi, FontStyle.Regular), stateColor, new Rectangle(textX, r.Y + Px(29), textW, Px(18)));
                var ib = new RectangleF(r.Right - Px(16) - iconW, r.Y + (r.Height - iconW) / 2f, iconW, iconW);
                bool hand = x.IsSelf ? s.HandRaised : x.HandRaised;
                if (hand) _w.FillListIcon(g, HandPath, RectangleF.Inflate(ib, -Px(2), -Px(2)), p.Accent);
                else _w.FillListIcon(g, muted ? MicOffPath : MicPath, ib, speaking ? p.Success : forced ? p.Danger : p.TextDim);
            }
        }
    }
}
