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
    /// with the same preloaded list so it navigates exactly these stories. Themed like the other profile dialogs.</summary>
    internal sealed class StoriesGridForm : Form
    {
        private readonly TelegramService _service;
        private readonly StoryPeerRef _peer;
        private readonly List<StoryItem> _stories;
        private readonly Color _accent;
        private readonly Func<long, Image> _avatar;

        public StoriesGridForm(TelegramService service, StoryPeerRef peer, List<StoryItem> stories,
                               bool dark, Color accent, Func<long, Image> avatarGetter)
        {
            _service = service; _peer = peer; _stories = stories ?? new List<StoryItem>(); _accent = accent; _avatar = avatarGetter;

            Color bg = dark ? Color.FromArgb(40, 40, 44) : Color.FromArgb(248, 248, 250);

            Text = "Posted stories";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(400, 560 + ThemedChrome.BarH);
            var content = ThemedChrome.Apply(this, "Posted stories", accent, dark);
            var scroll = ScrollHost.Wrap(content, dark, accent);
            scroll.BackColor = bg;

            var grid = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(372, 0),   // pin width → wrap into rows (4/row), auto height
                MaximumSize = new Size(372, 0),
                Location = new Point(12, 10),
                BackColor = bg
            };
            for (int i = 0; i < _stories.Count; i++)
            {
                int idx = i;   // capture → the viewer deep-links here
                var thumb = new StoryThumb(_service, _stories[i], dark) { Margin = new Padding(0, 0, 6, 6) };
                thumb.Clicked += () => OpenViewer(idx);
                grid.Controls.Add(thumb);
            }
            scroll.Controls.Add(grid);
        }

        private void OpenViewer(int idx)
        {
            if (_peer == null) return;
            var refs = new List<StoryPeerRef> { _peer };
            using (var viewer = new StoryViewerForm(_service, refs, 0, _avatar, _accent, idx, _stories))
                viewer.ShowDialog(this);
        }
    }
}
