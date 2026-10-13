using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;

namespace TelegArm.UI
{
    /// <summary>The full "Posted stories" gallery for one peer — a scrollable, wrapping grid of EVERY posted/pinned
    /// story (the profile shows only a preview row). Tap a tile → the existing full-screen viewer, deep-linked to it
    /// with the same preloaded list so it navigates exactly these stories. Themed like the other profile dialogs.
    ///
    /// Redesign step 2.6b: a <see cref="DialogForm"/> (was a plain Form + ThemedChrome.Apply). The grid scrolls by itself,
    /// so it is the dialog's OWN body (<c>UseOwnBody</c>): a <see cref="ScrollHost"/>-wrapped panel (themed scrollbar, touch
    /// momentum) holding the wrapping grid. <paramref name="dark"/> is kept for the caller and no longer read — the tiles
    /// take the palette's mode, and the rest follows the theme live. The footer's Close (or Esc / the title bar's close)
    /// dismisses it.</summary>
    internal sealed class StoriesGridForm : DialogForm
    {
        // The grid is pinned to this logical width so it wraps into 4 tiles per row (4 x (86 + 6) = 368 fits 372).
        private const int GridWidth = 372, GridLeft = 12, GridTop = 10, TileGap = 6;

        private readonly TelegramService _service;
        private readonly StoryPeerRef _peer;
        private readonly List<StoryItem> _stories;
        private readonly Color _accent;
        private readonly Func<long, Image> _avatar;
        private readonly Panel _host;
        private readonly NoNativeScrollPanel _scroll;
        private readonly FlowLayoutPanel _grid;

        public StoriesGridForm(TelegramService service, StoryPeerRef peer, List<StoryItem> stories,
                               bool dark, Color accent, Func<long, Image> avatarGetter)
            : base("Posted stories", 408, 640)
        {
            _service = service; _peer = peer; _stories = stories ?? new List<StoryItem>(); _accent = accent; _avatar = avatarGetter;

            var p = ThemeHelper.Palette;
            _host = new Panel { BackColor = p.Window };
            _scroll = ScrollHost.Wrap(_host, p.Dark, p.Accent);
            _scroll.BackColor = p.Window;

            _grid = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(Px(GridWidth), 0),   // pin width → wrap into rows (4/row), auto height
                MaximumSize = new Size(Px(GridWidth), 0),
                Location = new Point(Px(GridLeft), Px(GridTop)),
                BackColor = p.Window
            };
            int gap = Px(TileGap);
            for (int i = 0; i < _stories.Count; i++)
            {
                int idx = i;   // capture → the viewer deep-links here
                var thumb = new StoryThumb(_service, _stories[i], p.Dark)
                {
                    Size = new Size(Px(StoryThumb.W), Px(StoryThumb.H)),
                    Margin = new Padding(left: 0, top: 0, right: gap, bottom: gap)
                };
                thumb.Clicked += () => OpenViewer(idx);
                _grid.Controls.Add(thumb);
            }
            _scroll.Controls.Add(_grid);
            UseOwnBody(_host);

            CancelButton = AddFooterButton("Close", RoundedButtonKind.Neutral, DialogResult.Cancel);
        }

        private void OpenViewer(int idx)
        {
            if (_peer == null) return;
            var refs = new List<StoryPeerRef> { _peer };
            using (var viewer = new StoryViewerForm(_service, refs, 0, _avatar, _accent, idx, _stories))
                viewer.ShowDialog(this);
        }

        /// <summary>Runs from the base constructor too, before the fields exist — hence the null check.</summary>
        protected override void ApplyDialogTheme()
        {
            base.ApplyDialogTheme();
            if (_grid == null) return;
            var p = ThemeHelper.Palette;
            _host.BackColor = p.Window;
            _scroll.BackColor = p.Window;
            _grid.BackColor = p.Window;
            _grid.Invalidate(true);
        }
    }
}
