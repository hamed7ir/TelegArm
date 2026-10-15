using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>Redesign step 3.8b — the interface-scale slider's preview: one sample message bubble, drawn at the scale the
    /// thumb is over. Dragging changes only this (the plan: "drag previews a sample bubble only"); the windows rescale once,
    /// on release. The row is as tall as the bubble at the highest scale the slider can reach, so its height never changes
    /// mid-drag. Not interactive.
    /// ⚠ The window's DPI already includes the current scale: the sample is drawn at dpi × preview / current, so the page
    ///   must call <see cref="SetRange"/> again after every scale change (and on a move to another screen: the fit cap,
    ///   and so the current effective scale, is per screen).</summary>
    public sealed class ScalePreviewRow : SettingRow
    {
        private const string Sample = "Hi! Messages will look like this.", Time = "12:00";
        private const float TextPt = 10.5f, TimePt = 8f;
        private const int PadY = 12, BubbleX = 10, BubbleY = 6, TimeGap = 8, Radius = 14;
        private int _current = 100, _max = 200, _preview = 100;

        public ScalePreviewRow() : base("Preview", false)
        {
            AccessibleRole = AccessibleRole.Graphic;
            Refit();
        }

        /// <summary>The scale the window is at now (its DPI already includes it) and the highest the slider can pick.</summary>
        public void SetRange(int currentPct, int maxPct)
        {
            currentPct = Math.Max(1, currentPct);
            maxPct = Math.Max(currentPct, maxPct);
            if (currentPct == _current && maxPct == _max) return;
            _current = currentPct;
            _max = maxPct;
            if (!Dpi.Busy) Refit();
            Invalidate();
        }

        /// <summary>The scale the sample is drawn at.</summary>
        public int PreviewPercent
        {
            get { return _preview; }
            set { if (value == _preview) return; _preview = value; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); }
        }

        private int DpiFor(int pct, int dpi) { return Math.Max(1, (int)Math.Round(dpi * (double)pct / _current)); }

        private static Size BubbleSize(int pd)
        {
            var text = FontHelper.UiAt(TextPt, pd);   // shared: never dispose
            var time = FontHelper.UiAt(TimePt, pd);
            var ts = TextRenderer.MeasureText(Sample, text, Size.Empty, BaseFlags | TextFormatFlags.SingleLine);
            var tm = TextRenderer.MeasureText(Time, time, Size.Empty, BaseFlags | TextFormatFlags.SingleLine);
            return new Size(ts.Width + Dpi.Px(TimeGap, pd) + tm.Width + 2 * Dpi.Px(BubbleX, pd), ts.Height + 2 * Dpi.Px(BubbleY, pd));
        }

        public override int HeightFor(int width, int dpi) { return BubbleSize(DpiFor(_max, dpi)).Height + 2 * Dpi.Px(PadY, dpi); }

        protected override string AccessibleValueText { get { return _preview + "%"; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = ThemeHelper.Palette;
            var g = e.Graphics;
            int dpi = Dpi.Of(this), pd = DpiFor(_preview, dpi), padX = Dpi.Px(PadX, dpi);
            PaintBack(g, p);
            var size = BubbleSize(pd);
            var r = new Rectangle(padX, Dpi.Px(PadY, dpi), Math.Min(size.Width, Width - 2 * padX), size.Height);
            if (r.Width <= 0 || r.Height <= 0) return;

            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = DrawHelper.RoundedRect(r, Dpi.Px(Radius, pd)))
            using (var b = new SolidBrush(p.BubbleIn)) g.FillPath(b, path);
            g.SmoothingMode = mode;

            var text = FontHelper.UiAt(TextPt, pd);   // shared: never dispose
            var time = FontHelper.UiAt(TimePt, pd);
            int bx = Dpi.Px(BubbleX, pd), by = Dpi.Px(BubbleY, pd);
            var tm = TextRenderer.MeasureText(Time, time, Size.Empty, BaseFlags | TextFormatFlags.SingleLine);
            var timeRect = new Rectangle(r.Right - bx - tm.Width, r.Bottom - by - tm.Height, tm.Width, tm.Height);
            var textRect = new Rectangle(r.Left + bx, r.Top + by, Math.Max(1, timeRect.Left - Dpi.Px(TimeGap, pd) - r.Left - bx), r.Height - 2 * by);
            TextRenderer.DrawText(g, Sample, text, textRect, p.BubbleInText, BaseFlags | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Time, time, timeRect, p.BubbleInTime, BaseFlags | TextFormatFlags.SingleLine);
        }
    }
}
