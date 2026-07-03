using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MaterialSkin.Controls;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Minimal full-screen-ish viewer for profile photos: a zoom/pan panel plus ◀ ▶
    /// navigation when there are older photos. Owns and disposes the images it's given.
    /// </summary>
    public class ProfilePhotoViewer : MaterialForm
    {
        private readonly List<Image> _images;
        private int _index;
        private ZoomableImagePanel _panel;
        private MediaControlButton _prev, _next;
        private Label _counter;

        public ProfilePhotoViewer(List<Image> images)
        {
            _images = images ?? new List<Image>();

            FormStyle = FormStyles.ActionBar_None;
            Text = "Photo";
            TelegArm.Helpers.ThemedChrome.SetAppIcon(this);   // app icon in the taskbar / Alt-Tab
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 640);
            MaximizeBox = true;
            Sizable = true;
            BackColor = Color.FromArgb(20, 20, 20);
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Left) ShowAt(_index - 1);
                else if (e.KeyCode == Keys.Right) ShowAt(_index + 1);
            };

            _panel = new ZoomableImagePanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 20) };
            Controls.Add(_panel);

            if (_images.Count > 1)
            {
                _prev = new MediaControlButton { Icon = MediaButtonIcon.Previous, BackColor = Color.FromArgb(20, 20, 20) };
                _next = new MediaControlButton { Icon = MediaButtonIcon.Next, BackColor = Color.FromArgb(20, 20, 20) };
                _counter = new Label { ForeColor = Color.White, BackColor = Color.Transparent, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
                _prev.Click += (s, e) => ShowAt(_index - 1);
                _next.Click += (s, e) => ShowAt(_index + 1);
                Controls.Add(_prev); Controls.Add(_next); Controls.Add(_counter);
                _prev.BringToFront(); _next.BringToFront(); _counter.BringToFront();
                LayoutNav();
                Resize += (s, e) => LayoutNav();
            }

            ShowAt(0);
        }

        private void LayoutNav()
        {
            if (_prev == null) return;
            int y = ClientSize.Height - 48;
            _prev.Location = new Point(ClientSize.Width / 2 - 80, y);
            _next.Location = new Point(ClientSize.Width / 2 + 44, y);
            _counter.SetBounds(ClientSize.Width / 2 - 36, y, 72, 36);
        }

        private void ShowAt(int i)
        {
            if (_images.Count == 0) return;
            if (i < 0) i = 0;
            if (i > _images.Count - 1) i = _images.Count - 1;
            _index = i;
            _panel.Image = _images[_index];
            if (_counter != null) _counter.Text = (_index + 1) + " / " + _images.Count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var im in _images) { try { im.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
