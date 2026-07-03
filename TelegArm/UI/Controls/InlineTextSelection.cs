using System;
using System.Drawing;
using System.Windows.Forms;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// The ONE shared text-selection engine over an <see cref="InlineText"/> instance — reused by BOTH chat
    /// bubbles (MessageBubbleControl) and the profile description (RichInfoLabel). It tracks a character range
    /// [anchor..caret] within a single InlineText (within-bubble scope), distinguishes a click from a drag,
    /// drives the RTL-aware highlight paint, and exposes the selected text for copy. Mouse drag selects; on RT
    /// the host arms selection via the context menu ("Select Text") since touch-drag is consumed for panning.
    /// </summary>
    public sealed class InlineTextSelection
    {
        private readonly Control _host;
        private InlineText _rich;
        private int _anchor = -1, _caret = -1;
        private Point _downLocal;
        private bool _down, _dragging;
        private const int DragThreshold = 4;   // px before a press becomes a selection drag (vs a click)

        public InlineTextSelection(Control host) { _host = host; }

        /// <summary>Bind to the host's current InlineText (call whenever it is rebuilt); clears any selection.</summary>
        public void Attach(InlineText rich) { _rich = rich; _anchor = _caret = -1; }

        public bool HasSelection => _rich != null && _anchor >= 0 && _caret >= 0 && _anchor != _caret;
        public bool HasText => _rich != null && _rich.SelLength > 0;
        public int Start => Math.Min(_anchor, _caret);
        public int End => Math.Max(_anchor, _caret);
        public string SelectedText => HasSelection ? _rich.GetRangeText(Start, End) : null;

        public void MouseDown(Point local)
        {
            if (_rich == null) return;
            _down = true; _dragging = false; _downLocal = local;
        }

        /// <summary>True while a selection drag is in progress — the host should treat the gesture as a selection
        /// (not a scroll/click).</summary>
        public bool MouseMove(Point local)
        {
            if (!_down || _rich == null) return false;
            if (!_dragging)
            {
                int dx = local.X - _downLocal.X, dy = local.Y - _downLocal.Y;
                if (dx * dx + dy * dy < DragThreshold * DragThreshold) return false;
                _dragging = true;
                _anchor = _rich.HitChar(_downLocal);
            }
            _caret = _rich.HitChar(local);
            _host.Invalidate();
            return true;
        }

        /// <summary>Ends the gesture. A completed DRAG keeps the selection (highlighted, until a later plain
        /// click) and returns true so the host SUPPRESSES the click (a link under the drag won't open). A plain
        /// click (no drag past the threshold) CLEARS the selection here and returns false so the host runs its
        /// normal entity-click. Call only for the LEFT button.</summary>
        public bool MouseUp()
        {
            bool wasDrag = _dragging;
            _down = false; _dragging = false;
            if (wasDrag && HasSelection) return true;   // drag completed → KEEP the selection; suppress the click
            Clear();                                     // plain click → dismiss any selection
            return false;
        }

        public void SelectAll()
        {
            if (_rich == null || _rich.SelLength <= 0) return;
            _anchor = 0; _caret = _rich.SelLength; _host.Invalidate();
        }

        public void Clear()
        {
            if (_anchor >= 0 || _caret >= 0) { _anchor = _caret = -1; _host.Invalidate(); }
        }

        /// <summary>Draw the highlight behind the selected chars (call BEFORE the InlineText text paint).</summary>
        public void Paint(Graphics g, int ox, int oy, Color sel)
        {
            if (HasSelection) _rich.PaintSelectionHighlight(g, ox, oy, Start, End, sel);
        }
    }
}
