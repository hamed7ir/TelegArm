using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI
{
    /// <summary>BATCH-TA-27 — ONE NOTIFICATION, DRAWN BY US.
    ///
    /// Everything one notification needs to say, and nothing it does not: who, what, how many. It is
    /// composition, not new technology — the avatar comes from the existing cache, the palette from the
    /// same two-branch dark/light pairs the dock uses, and the text from the SAME <see cref="NotifyInfo"/>
    /// the legacy balloon consumes, so the two channels cannot describe the same message differently.
    ///
    /// See <see cref="NotificationStack"/> for placement, per-chat identity and the suppression gate.</summary>
    internal sealed class NotificationWindow : Form
    {
        // ══════════════════════════════════════════════════════════════════════════════════════════
        //  W1 — NO FOCUS STEAL.  ⚠⚠ READ THIS BEFORE CHANGING ANY OF THE FOUR LINES BELOW. ⚠⚠
        // ══════════════════════════════════════════════════════════════════════════════════════════
        // A notification that eats a keystroke while someone is typing is worse than no notification, so
        // this is the first thing that had to be right. It was ALSO the thing the batch spec got wrong,
        // and the failure is not reachable by reasoning — it was found by running an 8-case matrix
        // (mdfiles/probes/NoActivateMatrix.cs) after the first cut visibly swallowed typed characters.
        //
        // MEASURED — probe owns the foreground with a caret in a TextBox, candidate window is shown,
        // then "abc" is typed. "KEPT" = the probe still owns it, which is what we want:
        //
        //   B  ShowWithoutActivation ALONE ................ fg KEPT  focus KEPT  typed 'abc'
        //   C  WS_EX_NOACTIVATE ALONE ..................... fg LOST  focus LOST  typed ''
        //   D  both ....................................... fg KEPT  focus KEPT  typed 'abc'
        //   E  D + TopMost ................................ fg LOST  focus LOST  typed ''      ← !!
        //   G  D + TopMost + raw ShowWindow(SW_SHOWNOACTIVATE)  fg LOST  focus LOST  typed ''  ← !!
        //
        // ★ THE `TopMost` PROPERTY IS THE FOCUS THIEF. `Form.TopMost`'s setter calls
        //   SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE) with NO **SWP_NOACTIVATE**, so assigning
        //   the property ACTIVATES the window. Case E is case D plus that one assignment. It defeats the
        //   ex-style AND the raw SW_SHOWNOACTIVATE call (case G), which is why the recipe the batch
        //   specified — "WS_EX_NOACTIVATE plus ShowWindow(SW_SHOWNOACTIVATE)" — would still have failed.
        //
        // ⚠ SO: **NEVER SET `TopMost` IN THIS FILE.** It is a plausible one-word "fix" for "my
        //   notification went behind the window", and reapplying it silently reintroduces the exact bug
        //   this comment exists to prevent — the same reason TA-26 DELETED IsEffectivelyMuted instead of
        //   leaving it unused. Topmost is achieved in GoTopMostWithoutActivating() below, by calling
        //   SetWindowPos ourselves WITH SWP_NOACTIVATE. Confirmed 3x consecutively
        //   (mdfiles/probes/NoActivateFix.cs cases 3/3b/3c): foreground KEPT, focus KEPT, WS_EX_TOPMOST
        //   genuinely set, and every typed character delivered.
        //
        // Why BOTH mechanisms, since B alone passed: they answer different questions. The override
        // governs the SHOW call (case C proves the ex-style alone does not stop Form.Show() activating);
        // the ex-style governs what happens when the user CLICKS the window afterwards, which W5 depends
        // on — a click must open the chat without the notification itself taking focus first.
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;   // keeps it out of Alt-Tab
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                                int X, int Y, int cx, int cy, uint uFlags);

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        // ── W2 — identity. The window IS the chat, not the message. ──────────────────────────────
        /// <summary>⚠ THE ACCOUNT TRAVELS WITH THE WINDOW, IT IS NOT READ FROM A SHARED SLOT.
        /// The balloon path keeps ONE `_lastNotified*` pair in MainForm, which is correct for a channel
        /// that can only show one thing at a time and WRONG the moment two chats each have a window: the
        /// second notification would overwrite the slot and clicking the first would open the wrong chat.
        /// It bites hardest in the multi-account case, because a background account's notification has to
        /// SWITCH ACCOUNTS before opening the chat — a bug that looks fine in every single-account test.</summary>
        public long AccountId { get; private set; }
        public long PeerId { get; private set; }
        /// <summary>The newest message this window stands for. Updated in place with the preview, so a
        /// click after four more messages arrive opens at the newest, not the one that opened the window.</summary>
        public int MessageId { get; private set; }

        /// <summary>⚠ NAMED `Clicked`, NOT `Activated`. `Form.Activated` already exists and fires when the
        /// window is ACTIVATED — which for a WS_EX_NOACTIVATE window is the one thing that must never
        /// happen at show time. A member that hides it would let a subscriber attach to the wrong event
        /// and get either silence or the opposite meaning.</summary>
        public event Action<NotificationWindow> Clicked;
        public event Action<NotificationWindow> Dismissed;

        // ── layout, in DESIGN pixels at 96 dpi; every use is scaled by _scale ─────────────────────
        private const int DesignW = 360, DesignH = 84;
        private const int Pad = 12, AvatarD = 44, TitleH = 20, LineH = 18, CloseBox = 18;
        private const int DismissMs = 6000, TickMs = 100;

        private readonly float _scale;
        private readonly bool _dark;
        private readonly Color _accent, _bg, _border, _fg, _sub;

        private string _title, _text;
        private Image _avatar;
        private long _avatarPeerId;
        private int _count = 1;

        private readonly Timer _timer;
        private int _remainingMs = DismissMs;
        private bool _hovering, _closeHot, _closing;
        private Rectangle _closeRect;

        public NotificationWindow(NotifyInfo info, bool dark, Color accent)
        {
            AccountId = info.AccountId;
            PeerId = info.PeerId;
            MessageId = info.MessageId;
            _title = info.Title;
            _text = info.Text;
            _avatar = info.Avatar;
            _avatarPeerId = info.AvatarPeerId;

            _dark = dark;
            _accent = accent;
            // Same two-branch palette the dock uses (MainForm.BuildDock) rather than a third set of
            // greys — a notification that does not look like the app is a notification from some OTHER app.
            _bg = dark ? Color.FromArgb(34, 34, 37) : Color.FromArgb(250, 250, 252);
            _border = dark ? Color.FromArgb(64, 64, 70) : Color.FromArgb(214, 214, 222);
            _fg = dark ? Color.FromArgb(234, 234, 238) : Color.FromArgb(28, 28, 32);
            _sub = dark ? Color.FromArgb(158, 158, 166) : Color.FromArgb(112, 112, 120);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MinimizeBox = MaximizeBox = false;
            BackColor = _bg;
            Cursor = Cursors.Hand;      // set ONCE — assigning it per MouseMove repaints the cursor needlessly
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            // ⚠ SIZE SCALES, PLACEMENT DOES NOT NEED TO. The process is deliberately SYSTEM-DPI aware
            //   (Program.cs:56 — MaterialSkin.2 has no per-monitor support), so Screen.WorkingArea is
            //   already in the same pixels we position in and the anchor maths is DPI-agnostic. The BOX
            //   is not: FontHelper returns POINT-sized fonts, which grow with DPI, so a fixed 360x84 at
            //   200% would clip the text. Reading DpiX from our own Graphics gives the factor the process
            //   actually sees — and correctly reports 96 when the DPI-unaware rescue is on, because then
            //   the OS scales the whole window instead.
            float dpi = 96f;
            try { using (var g = CreateGraphics()) dpi = g.DpiX; } catch { }
            _scale = dpi / 96f;
            Size = new Size(S(DesignW), S(DesignH));

            _timer = new Timer { Interval = TickMs };
            _timer.Tick += OnTick;
        }

        private int S(int designPx) { return (int)Math.Round(designPx * _scale); }

        /// <summary>W2 — a newer message in the SAME chat updates this window instead of opening another.
        /// A busy group sending forty messages produces ONE window, not forty queued windows outliving the
        /// burst. The dismiss timer restarts, because the newest message is a fresh reason to look.</summary>
        public void Update(NotifyInfo info)
        {
            _title = info.Title;
            _text = info.Text;
            MessageId = info.MessageId;                       // click opens at the NEWEST message
            if (info.Avatar != null) { _avatar = info.Avatar; _avatarPeerId = info.AvatarPeerId; }
            _count++;
            _remainingMs = DismissMs;
            Invalidate();
        }

        /// <summary>Shows WITHOUT activating, then raises to topmost WITHOUT activating. Order matters:
        /// SetWindowPos needs a handle, and Show() is what creates it.</summary>
        public void ShowNoActivate(Point location)
        {
            Location = location;
            Show();                       // ShowWithoutActivation => ShowWindow(SW_SHOWNOACTIVATE)
            GoTopMostWithoutActivating();
            _timer.Start();
        }

        /// <summary>The ONLY way this window becomes topmost. See the W1 block above for why the
        /// `TopMost` property must never be used here.</summary>
        private void GoTopMostWithoutActivating()
        {
            try
            {
                if (IsHandleCreated)
                    SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { /* placement is cosmetic; never let it kill the notification */ }
        }

        /// <summary>Moves the window without activating it (the stack re-anchors on every show).</summary>
        public void MoveTo(Point location)
        {
            if (Location != location) Location = location;
            GoTopMostWithoutActivating();
        }

        // ── W5 — auto-dismiss, PAUSED on hover ───────────────────────────────────────────────────
        // A real pause, not a restart: the remaining time is what is held, so moving the pointer across a
        // notification you have nearly finished reading does not silently grant it another full 6 seconds.
        private void OnTick(object sender, EventArgs e)
        {
            if (_hovering) return;                 // paused — the clock simply does not advance
            _remainingMs -= TickMs;
            if (_remainingMs <= 0) Dismiss();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovering = true;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovering = false;                     // resumes from where it stopped
            if (_closeHot) { _closeHot = false; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hot = _closeRect.Contains(e.Location);
            if (hot != _closeHot) { _closeHot = hot; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (_closeRect.Contains(e.Location)) { Dismiss(); return; }
            // W5 — an INTENTIONAL activation, which is not what W1 forbids. W1 bans stealing focus at SHOW
            // time, when the user did not ask for anything; this is the user clicking the notification and
            // expecting the conversation. The handler (MainForm) does the account switch + chat open.
            var h = Clicked;
            if (h != null) h(this);
            Dismiss();
        }

        public void Dismiss()
        {
            if (_closing) return;
            _closing = true;
            try { _timer.Stop(); } catch { }
            var h = Dismissed;
            if (h != null) h(this);
            try { Close(); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _timer.Dispose(); } catch { }
                // ⚠ The avatar is BORROWED from the shared AvatarStore cache — do NOT dispose it. Every
                //   other surface draws the same Image instance; disposing it here would blank the chat
                //   list and the header the next time they paint.
                _avatar = null;
            }
            base.Dispose(disposing);
        }

        // ── W6 — owner-painted, in the app's own language ────────────────────────────────────────
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = ClientRectangle;

            using (var b = new SolidBrush(_bg)) g.FillRectangle(b, r);

            // A SQUARE window with a 1px border, deliberately. A rounded window needs a Region, and a
            // Region's edge is not antialiased — on a notification that is created and destroyed
            // constantly that trades a crisp border for jagged corners, and costs a region rebuild each
            // time. The card idiom elsewhere in the app (ProxyLinkForm) is the same 1px border.
            using (var p = new Pen(_border)) g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
            // A 3px accent spine on the left edge: at a glance, in the corner of the eye, this is what
            // says "TelegArm" before any text is read.
            using (var b = new SolidBrush(_accent)) g.FillRectangle(b, 0, 1, S(3), r.Height - 2);

            int pad = S(Pad), d = S(AvatarD);
            var circle = new Rectangle(pad, (r.Height - d) / 2, d, d);
            DrawAvatar(g, circle);

            int textX = circle.Right + pad;
            int closeW = S(CloseBox);
            _closeRect = new Rectangle(r.Width - pad - closeW, S(8), closeW, closeW);
            DrawClose(g, _closeRect);

            int titleW = _closeRect.Left - textX - S(6);
            using (var f = FontHelper.For(_title ?? "", 10f, FontStyle.Bold))
                TextRenderer.DrawText(g, _title ?? "", f, new Rectangle(textX, S(10), titleW, S(TitleH)), _fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                    | TextFormatFlags.EndEllipsis);

            // W2 — the count, so a burst reads as a burst rather than as one stale message.
            string more = _count > 1 ? "+" + (_count - 1) + " more" : null;
            int moreW = 0;
            if (more != null)
                using (var mf = FontHelper.Ui(8.5f, FontStyle.Bold))
                    moreW = TextRenderer.MeasureText(g, more, mf).Width + S(8);

            int bodyTop = S(10 + TitleH + 2);
            int bodyH = S(LineH) * 2;
            using (var f = FontHelper.For(_text ?? "", 9f))
                TextRenderer.DrawText(g, _text ?? "", f,
                    new Rectangle(textX, bodyTop, r.Width - textX - pad - moreW, bodyH), _sub,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (more != null)
                using (var mf = FontHelper.Ui(8.5f, FontStyle.Bold))
                    TextRenderer.DrawText(g, more, mf,
                        new Rectangle(r.Width - pad - moreW, bodyTop + S(LineH), moreW, S(LineH)), _accent,
                        TextFormatFlags.Right | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
        }

        /// <summary>The chat-list avatar, exactly: the cached photo clipped to a circle, or the per-peer
        /// colour with an initial. ⚠ CACHE ONLY — the notification path NEVER fetches. A notification is
        /// already late by the time it is drawn, and a network call here would make it later and could
        /// fire while the app is otherwise idle.</summary>
        private void DrawAvatar(Graphics g, Rectangle circle)
        {
            if (_avatar != null)
            {
                try { DrawHelper.DrawCircularImage(g, circle, _avatar); return; }
                catch { /* fall through to the initial */ }
            }
            using (var b = new SolidBrush(DrawHelper.AvatarColor(_avatarPeerId != 0 ? _avatarPeerId : PeerId)))
                g.FillEllipse(b, circle);
            string t = _title ?? "";
            // Skip the "@ " mention marker so a mention shows the CHAT's initial, not "@".
            if (t.StartsWith("@ ")) t = t.Substring(2);
            string initial = t.Length > 0 ? t.Substring(0, 1).ToUpperInvariant() : "?";
            // ⚠ NOT `14f * _scale`. FontHelper sizes are in POINTS, and GDI+ converts points to pixels
            //   using the device DPI — so the glyph ALREADY grows with DPI. The circle grows because it is
            //   a pixel box run through S(); multiplying the font too would scale it twice.
            using (var f = FontHelper.Ui(14f, FontStyle.Bold))
                TextRenderer.DrawText(g, initial, f, circle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private void DrawClose(Graphics g, Rectangle box)
        {
            if (_closeHot)
                using (var b = new SolidBrush(Color.FromArgb(40, _accent))) g.FillEllipse(b, box);
            int inset = Math.Max(4, box.Width / 3);
            using (var p = new Pen(_closeHot ? _accent : _sub, Math.Max(1.4f, 1.4f * _scale)))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(p, box.Left + inset, box.Top + inset, box.Right - inset, box.Bottom - inset);
                g.DrawLine(p, box.Right - inset, box.Top + inset, box.Left + inset, box.Bottom - inset);
            }
        }
    }

    /// <summary>BATCH-TA-27/W6 — ONE construction of a notification, consumed by BOTH channels.
    ///
    /// ⚠ THIS TYPE EXISTS BECAUSE THERE USED TO BE TWO. The active path built its caption from
    /// `_allChats` and the background path from `BgPeerTitle` + the account name, each with its own copy
    /// of the "empty ⇒ 'New message', longer than 160 ⇒ truncate" rules. Two constructions of the same
    /// sentence is how the window and the balloon end up describing the same message differently — the
    /// bug class TA-20/S0 exists to prevent. MainForm.BuildNotification is now the only place that
    /// composes this, and the window and the balloon both read it.</summary>
    internal sealed class NotifyInfo
    {
        /// <summary>Which account received it. 0 or the active id ⇒ no switch on click.</summary>
        public long AccountId;
        public long PeerId;
        public int MessageId;
        /// <summary>Fully composed: "@ " prefix for a mention, "Account · Chat" for a background account.</summary>
        public string Title;
        public string Text;
        /// <summary>From the cache, possibly null — never fetched on this path.</summary>
        public Image Avatar;
        /// <summary>Which peer the initials-circle colour keys off (the avatar may be absent).</summary>
        public long AvatarPeerId;
    }
}
