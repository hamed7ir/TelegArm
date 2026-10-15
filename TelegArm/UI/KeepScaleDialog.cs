using System;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>Redesign step 3.8b — "Keep this size?" right after an interface-scale change, shown AT the new size: Keep,
    /// or Revert; with no answer it reverts by itself after 10 seconds, so a size that makes the app unusable cannot stick.
    /// Enter keeps, Esc reverts. True = keep.</summary>
    internal sealed class KeepScaleDialog : DialogForm
    {
        private const int W = 420, Seconds = 10;
        private readonly MessageRow _row;
        private readonly Timer _timer;
        private readonly int _previous;
        private int _left = Seconds;

        public static bool Ask(IWin32Window owner, int previousPct)
        {
            using (var d = new KeepScaleDialog(previousPct))
                return d.ShowDialog(owner) == DialogResult.Yes;
        }

        private KeepScaleDialog(int previousPct) : base("Interface scale", W, 200)
        {
            _previous = previousPct;
            _row = new MessageRow(Message()) { Font = FontHelper.UiAt(10.5f, WindowDpiValue) };   // shared: never dispose
            PopulateBody(_row);

            // First added = rightmost: Keep, then Revert to its left.
            var keep = AddFooterButton("Keep", RoundedButtonKind.Primary, DialogResult.Yes);
            var revert = AddFooterButton("Revert", RoundedButtonKind.Neutral, DialogResult.No);
            AcceptButton = keep; CancelButton = revert; ActiveControl = keep;

            int bodyWidth = Width - 2 - Px(11);
            int bodyH = _row.HeightFor(bodyWidth, WindowDpiValue) + Px(RowGap) * 2;
            Size = new Size(Width, Bar.Height + bodyH + Px(FooterHeight) + 1);

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) =>
            {
                _left--;
                if (_left <= 0) { _timer.Stop(); DialogResult = DialogResult.No; return; }
                _row.Text = Message();
                _row.Invalidate();
            };
            Shown += (s, e) => _timer.Start();
            FormClosed += (s, e) => _timer.Stop();
        }

        private string Message()
        {
            return "Keep this size? It goes back to " + _previous + "% in " + _left + (_left == 1 ? " second." : " seconds.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _timer.Stop(); _timer.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
