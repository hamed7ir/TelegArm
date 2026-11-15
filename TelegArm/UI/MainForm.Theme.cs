using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;
using Message = TL.Message;

namespace TelegArm.UI
{
    public partial class MainForm
    {
        private void ApplyTheme()
        {
            _dark = ThemeHelper.IsDark;   // resolved (System → OS, else the override)
            _accent = ThemeHelper.GetWindowsAccentColor();
            ApplyThemeColors();
        }

        private void ApplyThemeColors()
        {
            // Redesign 2.7a: nothing to push into MaterialSkin any more — AppForm recolours its own chrome on ThemeChanged
            // and ApplyPanelColors does the panes.
        }

        private void ApplyPanelColors()
        {
            // Redesign 2.7b: pane surfaces and dividers from the chrome tokens (they inherited the form's Window colour).
            var pal = ThemeHelper.PaletteFor(_dark);
            if (_split != null)
            {
                _split.BackColor = pal.Divider;          // the 1 px splitter between the panes
                _split.Panel1.BackColor = pal.Sidebar;   // search row, folders and list: one surface
                _split.Panel2.BackColor = pal.Window;    // bars and the composer row; the message list paints ChatBg
            }
            if (_headerBar != null) _headerBar.BackColor = pal.Header;
            if (_headerDivider != null) _headerDivider.BackColor = pal.Divider;
            if (_messagePanel != null && _selectedChat == null) _messagePanel.Invalidate();   // the empty-state pill
            _searchBox?.Recolor();
            _messageInput?.Recolor();
            if (_chatListPanel != null)
                _chatListPanel.BackColor = ThemeHelper.PaletteFor(_dark).ListBg;
            if (_messagePanel != null)
                _messagePanel.BackColor = ThemeHelper.PaletteFor(_dark).ChatBg;
            foreach (var sb in _scrollBars)
            {
                sb.IsDark = _dark;
                sb.AccentColor = _accent;
                if (sb.Parent != null) sb.Parent.BackColor = ThemeHelper.PaletteFor(_dark).ChatBg;
                sb.Invalidate();
            }
            // SCROLLBTN-REGION: the jump button was themed only at construction — recolor it on live
            // theme flips like every other themed surface (paint reads IsDark/AccentColor per frame).
            if (_jumpBtn != null)
            {
                _jumpBtn.IsDark = _dark;
                _jumpBtn.AccentColor = _accent;
                _jumpBtn.Invalidate();
                foreach (var jb in new[] { _mentionBtn, _reactionBtn })   // T2
                    if (jb != null) { jb.IsDark = _dark; jb.AccentColor = _accent; jb.Invalidate(); }
            }

            // BATCH-TA-16d/D5 — recolor the proxy pill on the SAME live path. The jump button shipped a bug
            // where it was themed at CONSTRUCTION ONLY, so a theme flip left it stale; that is exactly why
            // this belongs here and not in BuildLeftPanel.
            if (_proxyPill != null)
            {
                _proxyPill.IsDark = _dark;
                _proxyPill.AccentColor = _accent;   // the ThemeHelper/ApplyTheme accent
                _proxyPill.Invalidate();
            }
            // Chat header uses the theme background (accent reverted); repaint the avatar on theme/accent change.
            if (_headerAvatar != null) _headerAvatar.Invalidate();
            // BUBBLE-DATETIME: the floating date pill is accent-driven — refresh its accent live.
            if (_dateFlyout != null) { _dateFlyout.Accent = _accent; _dateFlyout.IsDark = _dark; _dateFlyout.Invalidate(); }
        }

        private void OnSystemThemeChanged()
        {
            if (IsDisposed) return;
            BeginInvoke((Action)(() =>
            {
                ApplyTheme();      // re-read system dark/accent
                ApplyPanelColors();
                RefreshThemedControls();
                Invalidate(true);
            }));
        }

        /// <summary>Pushes the current accent/dark state into the custom-painted controls.</summary>
        private void RefreshThemedControls()
        {
            if (_hamburger != null) { _hamburger.ForeColor = ThemeHelper.PaletteFor(_dark).AccentGlyph; _hamburger.Invalidate(); }   // repaint the drawn icon with the current accent
            if (_chatSearchBtn != null) _chatSearchBtn.Invalidate();   // INCHAT-SEARCH: repaint the drawn magnifier with the current accent
            if (_chatMenuBtn != null)
            {
                // The hover tint is a one-shot property, so unlike the drawn glyph it does NOT follow a live
                // accent change on its own — re-assert it here, where every other themed control is refreshed.
                _chatMenuBtn.FlatAppearance.MouseOverBackColor = Color.Transparent;   // the tint is painted (and faded) by PaintButtonBackdrop
                _chatMenuBtn.Invalidate();
            }
            if (_callBtn != null)
            {
                _callBtn.FlatAppearance.MouseOverBackColor = Color.Transparent;   // the tint is painted (and faded) by PaintButtonBackdrop   // the one-shot hover tint, as _dockBtn
                _callBtn.Invalidate();
            }
            if (_backBtn != null)
            {
                _backBtn.FlatAppearance.MouseOverBackColor = Color.Transparent;   // the tint is painted (and faded) by PaintButtonBackdrop   // same one-shot gap as _dockBtn
                _backBtn.Invalidate();
            }
            if (_dockBtn != null)
            {
                // TA-23/D1c — same one-shot-property gap as _chatMenuBtn: re-assert the hover tint here,
                // because unlike the drawn glyph it does not follow a live accent change by itself.
                _dockBtn.FlatAppearance.MouseOverBackColor = Color.Transparent;   // the tint is painted (and faded) by PaintButtonBackdrop
                _dockBtn.Invalidate();
            }
            if (_dockEmoji != null)
            {
                // ⚠ The embedded panel bakes _dark/_accent at CONSTRUCTION — the popup never had this
                // problem because it is rebuilt on every open. Drop it so the next selection rebuilds it
                // in the new theme, and rebuild NOW if it is the pane on screen, so a theme switch can
                // never leave an empty dock.
                try { _dockBody.Controls.Remove(_dockEmoji); _dockEmoji.Dispose(); } catch { }
                _dockEmoji = null;
            }
            // Same reason for the profile: ProfileForm takes dark/accent at construction. (It also retint
            // itself via ThemeHelper.ThemeChanged, but its ROWS were built with the old colours.)
            DropDockProfile();
            if (_dockTabs != null) { SetDockPane(_dockPane); _dockTabs.Invalidate(); }   // accent underline + labels
            // TA-34/A4 — repaint the send disc on a live accent change. It reads ThemeHelper in OnPaint, so
            // it has no baked-in colour to re-assert, but it must still be told to redraw: RefreshThemedControls
            // fires on accent changes that do not raise ThemeChanged, which is exactly the gap the magnifier
            // has and the reason its hover tint goes stale.
            if (_sendButton != null) _sendButton.Invalidate();
            ShellNotify.RefreshBadgeColour(this);   // TA-35: the taskbar badge is accent-coloured now
            if (_attachButton != null) _attachButton.Invalidate(); // self-themes from ThemeHelper
            if (_footerBar != null) { _footerBar.AccentColor = _accent; _footerBar.IsDark = _dark; _footerBar.Invalidate(); }
            if (_threadJoinBar != null) { _threadJoinBar.AccentColor = _accent; _threadJoinBar.IsDark = _dark; _threadJoinBar.Invalidate(); }
            if (_miniBar != null) { _miniBar.AccentColor = _accent; _miniBar.IsDark = _dark; _miniBar.Invalidate(); }
            if (_pinnedBar != null) { _pinnedBar.AccentColor = _accent; _pinnedBar.IsDark = _dark; _pinnedBar.Invalidate(); }
            if (_replyTarget != null) UpdateReplyStrip(); // re-color the visible reply strip
            ThemeSelectionBar();

            // UI-FIX-T1: the ctor-only color sites (UI-AUDIT A.3) — re-push/re-derive on a live theme switch.
            if (_folderBar != null) _folderBar.BackColor = ThemeHelper.PaletteFor(_dark).ListBg;
            RebuildFolders();         // tab / rail colors derive from _dark/_accent at build → rebuilding IS the recolor (the one shown)
            RebuildTopicBar();        // FORUM-TOPICS: same immutable-chip pattern → rebuild to recolor on theme change (any folder mode)
            RebuildStoryTray();       // STORIES: chips are immutable (color baked at build) → rebuild IS the recolor
            if (_folderRail != null && FolderBarShown) _folderRail.BackColor = ThemeHelper.PaletteFor(_dark).FolderRailBg;   // hidden rail: recoloured when swapped in
            if (_botMenuButton != null)
            {
                _botMenuButton.BackColor = ThemeHelper.PaletteFor(_dark).BotMenuBg;
                _botMenuButton.ForeColor = ThemeHelper.PaletteFor(_dark).BotMenuFg;
                _botMenuButton.Invalidate();
            }
            RecolorConnectingUi();
            if (_switchOverlay != null)
            {
                _switchOverlay.BackColor = ThemeHelper.PaletteFor(_dark).OverlayBg;
                if (_switchOverlay.Visible) _switchOverlay.Invalidate();   // title text derives at paint time
            }
            UpdateHeaderStatus();   // status colors derive at invocation — recolors NOW, not on the next presence update

            foreach (Control c in _chatListPanel.Controls)
            {
                if (c is ChatListItemControl item) { item.AccentColor = _accent; item.IsDark = _dark; item.Invalidate(); }
                else if (c is Label lbl)   // search section headers (AddSectionHeader) — ctor-only ForeColor
                    lbl.ForeColor = ThemeHelper.PaletteFor(_dark).HintText;
            }
            foreach (Control c in _messagePanel.Controls)
            {
                if (c is MessageBubbleControl b) { b.AccentColor = _accent; b.IsDark = _dark; b.Invalidate(); }
                else if (c is VoiceBubbleControl v) { v.AccentColor = _accent; v.IsDark = _dark; v.Invalidate(); }
                else if (c is ServiceLineControl s) { s.IsDark = _dark; s.Invalidate(); }   // centered "X pinned…"/join lines
            }
        }

        // Night Mode pill: explicit Light/Dark (moves off System). Persists via SetThemeMode
        // (ThemeMode + Save) so it can't desync from the Settings → Appearance picker across restart.
        private void ToggleNightMode() => SetThemeMode(ThemeHelper.IsDark ? ThemeMode.Light : ThemeMode.Dark);
        private void SetThemeMode(ThemeMode mode)
        {
            // SetMode raises ThemeChanged → OnSystemThemeChanged re-applies the whole UI
            // (and any open MediaViewerForm, which also subscribes).
            ThemeHelper.SetMode(mode);
            AppSettings.Instance.ThemeMode = mode.ToString();
            AppSettings.Instance.Save();
        }

    }
}
