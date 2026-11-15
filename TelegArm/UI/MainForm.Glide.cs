using System;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    public partial class MainForm
    {
        // ── animations stage 3: the message list GLIDES (owner 2026-11-13, "make it feel smooth") ───────────────────
        // Jumping to the bottom, to a replied message, to a search result or a mention, and following a new message at
        // the bottom used to move the view in one step. They glide now: on the shared clock (Helpers.Anim), through the
        // scroller's own funnel (TouchScroller.ScrollTo raises Scrolled, so the themed scrollbar, the date pill and the
        // paging checks follow every frame, as they do for a finger). The goal is read again every frame (a thumbnail
        // that arrives, or another new message, moves the bottom). A long way is mostly jumped: only the last screen and
        // a half glides (a frame of content nobody can read is a wasted repaint on the Surface). A finger, the wheel, the
        // scrollbar or any jump by code stops it where it is. Off (Settings, Appearance, "Scrolling glides"): instant,
        // exactly as before. ScrollMessagesToBottom stays instant: it also re-pins the view after layout changes.

        private Tween _glide;
        private int _glideY;             // the offset the last frame set: anything else means someone else moved the view
        private Action _glideDone;
        private bool _glideToBottom;     // the running glide follows the bottom (a new message joins it, not the counter)
        private bool _glideHooked;

        private int MaxMessageScroll()
        {
            var p = _messagePanel;
            return p == null ? 0 : Math.Max(0, p.DisplayRectangle.Height - p.ClientSize.Height);
        }

        /// <summary>Glides the message list to the offset <paramref name="target"/> answers (a positive scroll offset,
        /// clamped). <paramref name="done"/> runs once, when it arrives (also at once when animations are off); a glide
        /// stopped by input never calls it.</summary>
        private void GlideMessagesTo(Func<int> target, int ms, Action done = null, bool toBottom = false)
        {
            CancelGlide();
            var panel = _messagePanel;
            if (panel == null || panel.IsDisposed) { if (done != null) done(); return; }
            TouchScroller.StopMomentum();
            HookGlideInput();
            Func<int> goal = () => Math.Max(0, Math.Min(target(), MaxMessageScroll()));
            int from = -panel.AutoScrollPosition.Y, to = goal();
            int far = Math.Max(Px(240), panel.ClientSize.Height * 3 / 2);
            if (Anim.Enabled(AnimKind.Scroll) && Math.Abs(to - from) > far)
            {
                TouchScroller.ScrollTo(panel, to - Math.Sign(to - from) * far);   // the far part at once
                from = -panel.AutoScrollPosition.Y;
            }
            if (from == to || !Anim.Enabled(AnimKind.Scroll))
            {
                if (from != to) TouchScroller.ScrollTo(panel, to);
                if (done != null) done();
                return;
            }
            int start = from;
            _glideY = from;
            _glideDone = done;
            _glideToBottom = toBottom;
            _glide = Anim.Run(panel, AnimKind.Scroll, ms, t =>
            {
                if (_glide == null && t < 1f) return;                               // stopped (by CancelGlide) this frame
                if (-panel.AutoScrollPosition.Y != _glideY) { CancelGlide(); return; } // moved by someone else: stop here
                int g = goal();
                int y = t >= 1f ? g : start + (int)Math.Round((g - start) * Anim.EaseOut(t));
                TouchScroller.ScrollTo(panel, y);
                _glideY = -panel.AutoScrollPosition.Y;
            }, () =>
            {
                var d = _glideDone;
                _glide = null; _glideDone = null; _glideToBottom = false;
                if (d != null) d();
                MaybeLoadOlderNearTop();   // it may have arrived near the loaded top (paging waits for the glide)
            });
        }

        /// <summary>Glides to the bottom (the latest message): the jump button, a sent message, a new message while the
        /// view is at the bottom.</summary>
        private void GlideMessagesToBottom(int ms, Action done = null)
        {
            if (_messagePanel == null) return;
            _messagePanel.PerformLayout();   // the scroll range includes the bubble just added
            GlideMessagesTo(MaxMessageScroll, ms, done, true);
        }

        /// <summary>Stops a running glide where it is (its done callback is dropped).</summary>
        private void CancelGlide()
        {
            var g = _glide;
            _glide = null; _glideDone = null; _glideToBottom = false;
            if (g != null && !g.Finished) g.Cancel();
        }

        /// <summary>A glide to the bottom is running: a new message arriving now follows it to the bottom.</summary>
        private bool GlidingToBottom { get { return _glide != null && _glideToBottom; } }

        private void HookGlideInput()
        {
            if (_glideHooked) return;
            _glideHooked = true;
            // a finger landed on, or the wheel turned over, THIS list (the chat list or Settings scrolling leave it alone)
            Action<ScrollableControl> stop = s => { if (_glide != null && !IsDisposed && (s == null || ReferenceEquals(s, _messagePanel))) CancelGlide(); };
            TouchScroller.UserInput += stop;
            FormClosed += (s, e) => { TouchScroller.UserInput -= stop; CancelGlide(); };
        }

        /// <summary>Where <paramref name="c"/> is centred in the view (a tall one: its top just under the top edge).</summary>
        private int CentredOffset(Control c)
        {
            var p = _messagePanel;
            int cur = -p.AutoScrollPosition.Y, viewH = p.ClientSize.Height;
            if (c == null || c.IsDisposed) return cur;
            int top = c.Top + cur;   // c.Top is in view coordinates (the view scrolled by cur)
            return c.Height >= viewH - Px(16) ? top - Px(8) : top - (viewH - c.Height) / 2;
        }
    }
}
