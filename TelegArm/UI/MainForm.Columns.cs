using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;

namespace TelegArm.UI
{
    public partial class MainForm
    {
        // ── Owner addition 2026-10-15: the adaptive one-column ("phone") layout ─────────────────────────────────────────
        // Below OneColumnBelow the window shows ONE column, as Telegram Desktop does: the chat list, or the open chat with a
        // back arrow (and the other chats' unread count) in its header. Esc / back returns to the list. In side-panel mode
        // the folder rail stays in one column too, beside the list AND beside the open chat (owner 2026-10-16: as in
        // Telegram Desktop, so it lives outside the split); only near the minimum width does it
        // give way to the horizontal tab strip over the list (FolderStripBelow), and it comes back when the window widens.
        // Both columns stay built; one is collapsed (SplitContainer.Panel1Collapsed / Panel2Collapsed), so nothing is
        // re-parented and no handle is re-created. The threshold is LOGICAL (the window's DPI and the interface scale).
        // The obvious user is the RT in portrait: 768 px wide at 100%.

        /// <summary>The window width (logical px) below which the layout is one column.</summary>
        private const int OneColumnBelow = 800;
        /// <summary>The narrowest the window may be: one column of chat (Telegram Desktop's own minimum is 380).</summary>
        private const int OneColumnMinWidth = 380;
        /// <summary>Side-panel mode: below this (logical px) the folder rail becomes the tab strip — near the minimum only.</summary>
        private const int FolderStripBelow = 480;

        private bool _oneColumn;
        private bool _folderStrip;          // side-panel mode, near the minimum: the tab strip stands in for the rail
        private bool _listInFront = true;   // one column: the list is the column shown (else the chat)
        private Button _backBtn;            // the chat header's back arrow, one column only
        private bool _sidebarMode;               // side-panel mode: the rail is a Dock.Left strip of Content, beside the split
        private Panel _folderBarHost;            // the tab strip's scrollbar host (the row it sits in opens in one column)
        private int _listWidthLogical = 300;     // the list column's width in two columns (BuildUi's 300, or where the user dragged it)

        /// <summary>From BuildUi. While the list column is collapsed and the window narrows, SplitContainer clamps the splitter
        /// (FixedPanel = Panel1) to a sliver and keeps it there on widening — found by the live rig: back in two columns the
        /// list was ~30 px. So the width is remembered here, only while two columns are really on screen and the window is
        /// wide enough that nothing was clamped, and put back by ApplyColumns.</summary>
        private void HookColumnSplitter()
        {
            _split.SplitterMoved += (s, e) =>
            {
                if (_oneColumn || Dpi.Busy || _split.Panel1Collapsed || _split.Panel2Collapsed || Width < Px(OneColumnBelow)) return;
                _listWidthLogical = Math.Max(240, (int)Math.Round(_split.SplitterDistance * (double)Dpi.Logical / WindowDpiValue));
            };
        }

        /// <summary>From the resize path and after a DPI change: picks the mode for the window's width now.</summary>
        private void UpdateColumnLayout()
        {
            if (_split == null || Dpi.Busy || WindowState == FormWindowState.Minimized) return;
            bool one = Width < Px(OneColumnBelow);
            if (one != _oneColumn)
            {
                _oneColumn = one;
                _listInFront = _selectedChat == null;   // narrowing keeps whatever was being read
                if (LogOn) Logger.Diag("[COLUMNS] " + (one ? "one column" : "two columns") + " w=" + Width + " chat=" + (_selectedChat != null));
            }
            bool strip = _sidebarMode && Width < Px(FolderStripBelow);
            if (strip != _folderStrip)
            {
                _folderStrip = strip;
                SwapFolderNavigator();
                if (LogOn) Logger.Diag("[COLUMNS] folders as " + (strip ? "tab strip" : "rail") + " w=" + Width);
            }
            ApplyColumns();
        }

        /// <summary>One column: bring the list (true) or the open chat (false) to the front. A no-op in two columns, apart
        /// from keeping the back arrow right.</summary>
        private void ShowColumn(bool list)
        {
            _listInFront = list;
            ApplyColumns();
        }

        private void ApplyColumns()
        {
            if (_split == null) return;
            bool chat = _oneColumn && _selectedChat != null && !_listInFront;
            // un-collapse first: a SplitContainer never has both panels collapsed, and setting one un-collapses the other
            if (!_oneColumn)
            {
                bool wasOne = _split.Panel1Collapsed || _split.Panel2Collapsed;
                _split.Panel1Collapsed = false; _split.Panel2Collapsed = false;
                if (wasOne) RestoreListWidth();
            }
            else if (chat) { _split.Panel2Collapsed = false; _split.Panel1Collapsed = true; }
            else { _split.Panel1Collapsed = false; _split.Panel2Collapsed = true; }
            UpdateBackButton();
        }

        private void RestoreListWidth()
        {
            int max = _split.Width - _split.Panel2MinSize - _split.SplitterWidth;
            int want = Math.Max(_split.Panel1MinSize, Math.Min(Px(_listWidthLogical), max));
            try { if (want > 0 && _split.SplitterDistance != want) _split.SplitterDistance = want; } catch { /* the window is mid-resize; the next resize applies it */ }
        }

        /// <summary>Side-panel mode only: the rail column, or (near the minimum width) the tab strip row.</summary>
        private void SwapFolderNavigator()
        {
            if (!_sidebarMode || _folderRail == null || _folderBar == null) return;
            _folderRail.Visible = !_folderStrip;
            if (_storyTrayLayout != null) SetRowPx(_storyTrayLayout, 2, _folderStrip ? 46 : 0);
            if (_folderBarHost != null) _folderBarHost.Visible = _folderStrip;
            RebuildFolders();   // the one now shown is rebuilt (only the shown one keeps its badges current)
        }

        /// <summary>Is the horizontal folder strip the navigator on screen? Tab mode always; side-panel mode near the minimum.</summary>
        private bool FolderBarShown { get { return _folderBar != null && (!_sidebarMode || _folderStrip); } }

        /// <summary>The header's back arrow: one level back — a comment thread to its chat, in-chat search to its results,
        /// a chat to the list (closing it, so nothing is read in a chat that is not on screen).</summary>
        private void OnBackClick()
        {
            if (_thread != null) { CloseThread(); return; }
            if (_inChatSearchEntry != null) { ShowColumn(true); return; }
            CloseOpenChat();
        }

        /// <summary>The unread count of every other chat (not archived), as the back arrow shows it.</summary>
        private int BackUnread()
        {
            if (_allChats == null) return 0;
            long open = _selectedChat != null ? _selectedChat.PeerId : 0;
            return _allChats.Where(c => !c.Archived && c.PeerId != open).Sum(c => Math.Max(0, c.UnreadCount));
        }

        private int _backUnreadShown = -1;

        /// <summary>Shown in one column with a chat open; as wide as the arrow plus its badge.</summary>
        private void UpdateBackButton()
        {
            if (_backBtn == null) return;
            bool show = _oneColumn && _selectedChat != null;
            if (_backBtn.Visible != show) _backBtn.Visible = show;
            if (!show) { _backUnreadShown = -1; return; }
            int n = BackUnread();
            if (n == _backUnreadShown) return;
            _backUnreadShown = n;
            int w = Px(44) + (n > 0 ? BackBadgeWidth(n) + Px(2) : 0);
            if (_backBtn.Width != w) _backBtn.Width = w;
            _backBtn.Invalidate();
        }

        private static string BackBadgeText(int n) { return n > 99 ? "99+" : n.ToString(); }

        private int BackBadgeWidth(int n)
        {
            return Math.Max(Px(18), TextRenderer.MeasureText(BackBadgeText(n), FontHelper.UiAt(7.5f, WindowDpiValue, FontStyle.Bold)).Width + Px(8));
        }

        private void PaintBackButton(Graphics g)
        {
            var area = new Rectangle(0, 0, Px(44), _backBtn.ClientSize.Height);
            var color = ThemeHelper.PaletteFor(_dark).AccentGlyph;
            DrawBackGlyph(g, area, color);
            int n = _backUnreadShown;
            if (n <= 0) return;
            string t = BackBadgeText(n);
            int bw = BackBadgeWidth(n), bh = Px(18);
            var badge = new Rectangle(area.Right - Px(6), (_backBtn.ClientSize.Height - bh) / 2, bw, bh);
            using (var bb = new SolidBrush(_accent))
            using (var pth = DrawHelper.RoundedRect(badge, bh / 2)) g.FillPath(bb, pth);
            TextRenderer.DrawText(g, t, FontHelper.UiAt(7.5f, WindowDpiValue, FontStyle.Bold), badge, ColorMath.OnColor(_accent),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        /// <summary>A left arrow in lines (a font arrow is tofu on RT 8.1), the weight of the other header glyphs.</summary>
        private static void DrawBackGlyph(Graphics g, Rectangle area, Color color)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int s = Math.Min(area.Width, area.Height);
            int side = HeaderGlyphBox(area);
            float cx = area.X + area.Width / 2f, cy = area.Y + area.Height / 2f, h = side / 2f;
            using (var pen = new Pen(color, Math.Max(1.5f, s * 0.05f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round,
                                                                          LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            {
                g.DrawLine(pen, cx - h, cy, cx + h, cy);
                g.DrawLines(pen, new[] { new PointF(cx - h + h * 0.75f, cy - h * 0.75f), new PointF(cx - h, cy), new PointF(cx - h + h * 0.75f, cy + h * 0.75f) });
            }
        }
    }
}
