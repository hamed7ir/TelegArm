using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Displays an image centered and scaled-to-fit, with mouse-wheel zoom
    /// (50%–300%), click-drag panning, and double-click to reset. The image is
    /// referenced, not owned — the caller disposes it.
    /// </summary>
    public class ZoomableImagePanel : Control
    {
        private const float MinZoom = 0.5f, MaxZoom = 3.0f, ZoomStep = 1.15f;

        private Image _image;
        private float _zoom = 1f;          // 1.0 == fit-to-panel
        private PointF _offset;            // pan offset in pixels
        private bool _dragging;
        private Point _dragStart;
        private PointF _offsetStart;

        public ZoomableImagePanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(18, 18, 18);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);
        }

        public Image Image
        {
            get { return _image; }
            set { _image = value; ResetView(); }
        }

        public void ResetView()
        {
            _zoom = 1f;
            _offset = PointF.Empty;
            Invalidate();
        }

        private float FitScale()
        {
            if (_image == null || _image.Width == 0 || _image.Height == 0) return 1f;
            return Math.Min((float)Width / _image.Width, (float)Height / _image.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            if (_image == null) return;

            float scale = FitScale() * _zoom;
            float w = _image.Width * scale;
            float h = _image.Height * scale;
            float x = (Width - w) / 2f + _offset.X;
            float y = (Height - h) / 2f + _offset.Y;

            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_image, x, y, w, h);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_image == null) return;
            float factor = e.Delta > 0 ? ZoomStep : 1f / ZoomStep;
            _zoom = Math.Max(MinZoom, Math.Min(MaxZoom, _zoom * factor));
            if (_zoom <= 1f) _offset = PointF.Empty; // recenter at/under fit
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragStart = e.Location;
                _offsetStart = _offset;
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                _offset = new PointF(_offsetStart.X + (e.X - _dragStart.X),
                                     _offsetStart.Y + (e.Y - _dragStart.Y));
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            ResetView();
        }
    }
}
