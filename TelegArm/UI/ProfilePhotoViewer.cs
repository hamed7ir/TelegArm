using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    /// <summary>
    /// Minimal full-screen-ish viewer for profile photos: a zoom/pan panel plus previous / next
    /// navigation when there are older photos. Owns and disposes the images it's given.
    ///
    /// Redesign step 2.2: an <see cref="AppForm"/> (was a Material-skinned form) — a resizable window with TelegArm's own title bar.
    /// The photo fills <c>Content</c>; with more than one photo a bar is DOCKED under it (previous, counter, next) instead
    /// of floating over the picture. Keys as before: Left / Right step through the photos, Esc closes. It is shown
    /// modally, so it cannot be minimised (a minimised modal window would leave its owner disabled).
    /// </summary>
    public class ProfilePhotoViewer : AppForm
    {
        private const int NavBarHeight = 48, NavButton = 36, NavCounterWidth = 72, NavGap = 8;

        private readonly List<Image> _images;
        private int _index;
        private ZoomableImagePanel _panel;
        private Panel _navBar;
        private MediaControlButton _prev, _next;
        private ThemedLabel _counter;

        public ProfilePhotoViewer(List<Image> images)
        {
            _images = images ?? new List<Image>();

            Text = "Photo";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(Px(640), Px(680));
            LogicalMinimumSize = new Size(320, 280);
            MinimizeBox = false;
            Bar.CanMinimize = false;

            _panel = new ZoomableImagePanel { Dock = DockStyle.Fill };
            Content.Controls.Add(_panel);   // Fill first: docking takes the last-added (the nav bar) first

            if (_images.Count > 1)
            {
                _navBar = new Panel { Dock = DockStyle.Bottom, Height = Px(NavBarHeight) };
                _prev = new MediaControlButton { Icon = MediaButtonIcon.Previous, Size = new Size(Px(NavButton), Px(NavButton)), AccessibleName = "Previous photo" };
                _next = new MediaControlButton { Icon = MediaButtonIcon.Next, Size = new Size(Px(NavButton), Px(NavButton)), AccessibleName = "Next photo" };
                _counter = new ThemedLabel { TextAlign = HorizontalAlignment.Center, Role = LabelRole.Body, PointSize = TextRoles.Points(TextRole.Body) };
                _prev.Click += (s, e) => ShowAt(_index - 1);
                _next.Click += (s, e) => ShowAt(_index + 1);
                _navBar.Controls.Add(_prev); _navBar.Controls.Add(_counter); _navBar.Controls.Add(_next);
                _navBar.SizeChanged += (s, e) => LayoutNav();
                Content.Controls.Add(_navBar);
                LayoutNav();
            }

            ApplyTheme();
            ShowAt(0);
        }

        /// <summary>Esc closes; Left / Right step through the photos. Taken here (before dialog-key processing) rather than
        /// from KeyDown, so they work whichever control has the focus — INFERENCE: after a click on a nav button the framework's
        /// arrow-key focus movement could otherwise take the arrow before KeyDown sees it. There is no text field here to
        /// need the arrows.</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.Left) { ShowAt(_index - 1); return true; }
            if (keyData == Keys.Right) { ShowAt(_index + 1); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>AppForm raises this on every theme change (it subscribes and unsubscribes ThemeHelper itself).</summary>
        protected override void OnThemeChanged() { ApplyTheme(); }

        private void ApplyTheme()
        {
            var p = ThemeHelper.Palette;
            if (_panel != null) { _panel.BackColor = p.SurfaceAlt; _panel.Invalidate(); }
            if (_navBar != null) _navBar.BackColor = p.Footer;
            if (_prev != null) _prev.BackColor = p.Footer;   // the buttons clear to their BackColor
            if (_next != null) _next.BackColor = p.Footer;
            if (_counter != null) _counter.Invalidate();
        }

        /// <summary>Centres previous / counter / next in the bar.</summary>
        private void LayoutNav()
        {
            if (_navBar == null) return;
            int b = Px(NavButton), cw = Px(NavCounterWidth), gap = Px(NavGap);
            int x = (_navBar.Width - (b + gap + cw + gap + b)) / 2, y = (_navBar.Height - b) / 2;
            _prev.SetBounds(x, y, b, b);
            _counter.SetBounds(x + b + gap, y, cw, b);
            _next.SetBounds(x + b + gap + cw + gap, y, b, b);
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
