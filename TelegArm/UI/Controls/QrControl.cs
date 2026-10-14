using System;
using System.Drawing;
using System.Windows.Forms;
using QRCoder;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Renders a QR code for a payload string. Uses QRCoder ONLY to compute the module matrix
    /// (QRCodeGenerator + QRCodeData.ModuleMatrix — pure managed, no native bits, ARM32-safe) and draws it
    /// itself with GDI+ FillRectangle, so it themes cleanly and never depends on QRCoder's System.Drawing
    /// renderer. The matrix already includes the 4-module quiet zone (so it scans).
    ///
    /// Redesign step 3.2: the modules scale from the control's size (the host sizes it); the logo pad's inset and
    /// radius are logical px at the window's DPI. At 96 DPI every value is exactly what it was.
    /// </summary>
    public sealed class QrControl : Control, IDpiAware
    {
        private bool[,] _matrix;
        private int _n;
        private int _dpi;                                          // the DPI the pad is drawn at; 0 = not parented yet
        private int CurDpi { get { return _dpi > 0 ? _dpi : Dpi.Of(this); } }

        public Color Dark { get; set; } = Color.Black;
        public Color Light { get; set; } = Color.White;

        /// <summary>Optional logo drawn (on a white rounded pad) over the QR center — like Telegram Desktop. Small
        /// enough (~22% side) to stay within the error-correction budget, so the code still scans.</summary>
        public Image CenterLogo { get; set; }

        public QrControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (Parent == null) return;
            int d = Dpi.Of(this);
            if (d != _dpi) { _dpi = d; Invalidate(); }
        }

        void IDpiAware.OnDpiChanged(int oldDpi, int newDpi) { _dpi = newDpi; Invalidate(); }

        /// <summary>Encodes <paramref name="text"/> and repaints. Empty/failed → blank (caller may show a spinner).</summary>
        public void SetPayload(string text)
        {
            if (string.IsNullOrEmpty(text)) { _matrix = null; if (!IsDisposed) Invalidate(); return; }
            try
            {
                using (var gen = new QRCodeGenerator())
                {
                    var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);   // Q = 25% recovery → room for a center logo
                    var mm = data.ModuleMatrix;
                    _n = mm.Count;
                    var m = new bool[_n, _n];
                    for (int r = 0; r < _n; r++)
                        for (int c = 0; c < _n; c++)
                            m[r, c] = mm[r][c];
                    _matrix = m;
                }
            }
            catch { _matrix = null; }
            if (!IsDisposed) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int side = Math.Min(Width, Height);
            int ox = (Width - side) / 2, oy = (Height - side) / 2;
            using (var lb = new SolidBrush(Light)) g.FillRectangle(lb, ox, oy, side, side);   // white card incl. quiet zone
            var m = _matrix;
            if (m == null || _n <= 0) return;

            float cell = side / (float)_n;
            using (var db = new SolidBrush(Dark))
                for (int r = 0; r < _n; r++)
                    for (int c = 0; c < _n; c++)
                        if (m[r, c])
                            g.FillRectangle(db, ox + (int)(c * cell), oy + (int)(r * cell),
                                (int)Math.Ceiling(cell), (int)Math.Ceiling(cell));   // ceil → no seams between modules

            // Center logo (like Telegram Desktop) on a white rounded pad — small enough for ECC-Q to still scan.
            var logo = CenterLogo;
            if (logo != null)
            {
                int dpi = CurDpi;
                int sz = (int)(side * 0.22f);
                int lx = ox + (side - sz) / 2, ly = oy + (side - sz) / 2;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int inset = Dpi.Px(7, dpi);                                 // 2 × inset keeps the pad centred (== 14 at 96)
                var pad = new Rectangle(lx - inset, ly - inset, sz + 2 * inset, sz + 2 * inset);
                using (var wb = new SolidBrush(Light)) using (var pth = DrawHelper.RoundedRect(pad, Dpi.Px(10, dpi))) g.FillPath(wb, pth);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(logo, new Rectangle(lx, ly, sz, sz));
            }
        }
    }
}
