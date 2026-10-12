using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MaterialSkin;
using MaterialSkin.Controls;
using TelegArm.Core;
using TelegArm.Helpers;
using TelegArm.UI.Controls;
using TL;
using Message = TL.Message;

namespace TelegArm.UI
{
    public partial class MainForm
    {
        // ── BATCH-TA-23/D1 — THE RIGHT-SIDE DOCK (shell only) ────────────────────────────────────
        /// <summary>Builds the dock as a Dock.Right strip INSIDE _split.Panel2.
        ///
        /// ⚠ WHY Panel2 AND NOT A NESTED SplitContainer. A nested splitter cannot open on a narrow
        /// window: the outer mins are already Panel1MinSize 240 + Panel2MinSize 360, and an inner
        /// Panel2MinSize for the dock would push the smallest workable width past this form's
        /// MinimumSize of 720 — so on the RT device the dock could never open at all. A docked strip has
        /// no nested min-size arithmetic.
        /// ⚠ AND WHY IT IS ADDED AFTER THE FILL LAYOUT. Docking resolves LAST-ADDED FIRST (measured in
        /// TA-21: three Dock.Right buttons land left-to-right in ADD order), so this strip claims its
        /// width and the Fill layout takes the remainder. Nothing above it had to move.
        /// ⚠ BOTH LAYOUT MODES ARE UNAFFECTED. sidebar-rail and folder-bar only ever build Panel1; a
        /// Panel2-side dock is therefore ONE code path, not two. That is the whole reason for this shape.
        /// ⚠ SplitterDistance IS NOT TOUCHED — it stays hardcoded at 300 (there is no persistence for it
        /// today, and this batch adds none).</summary>
        private void BuildDock()
        {
            Color bg = _dark ? Color.FromArgb(34, 34, 37) : Color.FromArgb(246, 246, 248);
            Color line = _dark ? Color.FromArgb(58, 58, 62) : Color.FromArgb(222, 222, 228);

            _dock = new Panel { Dock = DockStyle.Right, Width = DockWidth, Visible = false, BackColor = bg };
            _dock.Paint += (s, e) =>
            {
                using (var p = new Pen(_dark ? Color.FromArgb(58, 58, 62) : Color.FromArgb(222, 222, 228)))
                    e.Graphics.DrawLine(p, 0, 0, 0, _dock.Height);   // 1px seam so it reads as its own column
            };

            // The Info pane is an EMBEDDED ProfileForm and the Emoji pane an EMBEDDED EmojiPicker; both
            // dock Fill into this body, so it owns no surface of its own.
            _dockBody = new Panel { Dock = DockStyle.Fill, BackColor = bg };


            _dockTabs = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = bg };
            _dockTabs.Paint += (s, e) =>
            {
                using (var p = new Pen(line)) e.Graphics.DrawLine(p, 0, _dockTabs.Height - 1, _dockTabs.Width, _dockTabs.Height - 1);
            };
            _dockInfoTab = MakeDockTab("Info", DockPane.Info);
            _dockEmojiTab = MakeDockTab("Emoji", DockPane.Emoji);
            _dockTabs.Controls.Add(_dockInfoTab);
            _dockTabs.Controls.Add(_dockEmojiTab);
            _dockTabs.Resize += (s, e) => LayoutDockTabs();

            _dock.Controls.Add(_dockBody);   // Fill added first → takes the remainder
            _dock.Controls.Add(_dockTabs);   // Top
            _split.Panel2.Controls.Add(_dock);
            LayoutDockTabs();
            SetDockPane(DockPane.Info);
        }

        private Label MakeDockTab(string text, DockPane pane)
        {
            var l = new Label
            {
                Text = text, AutoSize = false, Height = 37, Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter, Font = FontHelper.Ui(9.5f),
                BackColor = Color.Transparent
            };
            l.Click += (s, e) => SetDockPane(pane);
            l.Paint += (s, e) =>
            {
                if (_dockPane != pane) return;
                using (var p = new Pen(_accent, 2f))
                    e.Graphics.DrawLine(p, 6, l.Height - 2, l.Width - 6, l.Height - 2);   // accent underline
            };
            return l;
        }

        /// <summary>Lays the visible tabs out across the strip. A HIDDEN tab takes no space — the
        /// remaining one fills — because an unavailable source is OMITTED, not greyed (TA-20/S0).</summary>
        private void LayoutDockTabs()
        {
            if (_dockTabs == null) return;
            var shown = new List<Label>();
            if (_dockInfoTab != null && _dockInfoTab.Visible) shown.Add(_dockInfoTab);
            if (_dockEmojiTab != null && _dockEmojiTab.Visible) shown.Add(_dockEmojiTab);
            if (shown.Count == 0) return;
            int w = _dockTabs.ClientSize.Width / shown.Count;
            for (int i = 0; i < shown.Count; i++)
            {
                shown[i].Left = i * w;
                shown[i].Width = i == shown.Count - 1 ? _dockTabs.ClientSize.Width - i * w : w;
                shown[i].Top = 0;
            }
            _dockTabs.Invalidate();
        }

        /// <summary>BATCH-TA-23/D3 — builds the Emoji pane ON FIRST USE, and it is THE COMPOSER'S PANEL:
        /// the same <see cref="EmojiPicker"/> type, with its Emoji / Stickers / GIFs tabs, its sticker-pack
        /// bar, its lazy loading and its caches. Not a second grid — a second grid is two things to fix
        /// every time either changes.
        /// Built lazily because it costs a catalog layout and (on the sticker/GIF tabs) network work, none
        /// of which should land in BuildUi's cold-start path.</summary>
        private void EnsureDockEmoji()
        {
            if (_dockEmoji != null || _dockBody == null || _service == null) return;
            var p = new EmojiPicker(_service, _dark, _accent, embedded: true)
            {
                TopLevel = false,                      // embed: it becomes an ordinary child control
                FormBorderStyle = FormBorderStyle.None,
                Dock = DockStyle.Fill
            };
            p.Picked += InsertEmoji;                   // the SAME handlers the popup uses
            p.DocumentPicked += SendDocument;
            _dockBody.Controls.Add(p);
            p.Show();                                  // a non-top-level form still needs this to lay out
            _dockEmoji = p;
        }

        private void SetDockPane(DockPane pane)
        {
            _dockPane = pane;
            if (pane == DockPane.Emoji) EnsureDockEmoji();
            if (_dockEmoji != null) _dockEmoji.Visible = pane == DockPane.Emoji;
            if (pane == DockPane.Info) EnsureDockProfile();
            if (_dockProfile != null) _dockProfile.Visible = pane == DockPane.Info;
            if (_dockInfoTab != null)
            {
                _dockInfoTab.ForeColor = pane == DockPane.Info ? _accent
                    : (_dark ? Color.FromArgb(170, 170, 176) : Color.FromArgb(105, 105, 112));
                _dockInfoTab.Invalidate();
            }
            if (_dockEmojiTab != null)
            {
                _dockEmojiTab.ForeColor = pane == DockPane.Emoji ? _accent
                    : (_dark ? Color.FromArgb(170, 170, 176) : Color.FromArgb(105, 105, 112));
                _dockEmojiTab.Invalidate();
            }
        }

        // ── BATCH-TA-24 — THE INFO PANE *IS* ProfileForm ──────────────────────────────────────────
        /// <summary>Hosts the REAL ProfileForm in the dock, for the open chat.
        ///
        /// ⚠ THIS REPLACED A HAND-BUILT "compact info pane" AND THAT WAS THE RIGHT CALL. The compact
        /// version showed an avatar, a name and a column of counts behind an "Open full profile" button —
        /// side by side with the reference it read as a stub, and it had already grown its own row painter,
        /// its own media-count fetch and its own phone/username rows. Every one of those was a second
        /// implementation of something ProfileForm already did properly. Now there is one profile, shown
        /// either as a modal or as a pane, and "Open full profile" is gone because there is nothing more to
        /// open.
        ///
        /// Rebuilt per PEER, not per call: constructing it runs LoadDetails (network), so a re-entry for
        /// the same chat is a no-op. Built only while the Info pane is actually visible.</summary>
        private void EnsureDockProfile()
        {
            if (_dockBody == null || _service == null) return;
            var entry = _selectedChat;
            if (entry == null) { DropDockProfile(); return; }
            if (_dockProfile != null && _dockProfilePeerId == entry.PeerId) return;

            DropDockProfile();
            // ⚠ contentWidth is the dock MINUS its own scrollbar strip: ProfileForm docks a ThemedScrollBar
            //   Right inside itself, and handing it the full width would overflow the flow by that strip and
            //   arm a horizontal scroll.
            var pf = new ProfileForm(_service, entry, GetCachedAvatar(entry.PeerId), DockWidth - 12)
            {
                TopLevel = false,
                FormBorderStyle = FormBorderStyle.None,
                Dock = DockStyle.Fill
            };
            pf.Avatars = _avatars;                          // member rows use the shared store
            pf.ForwardRequested += ForwardFromProfile;
            pf.ShowInChatRequested += ShowInChatFromProfile;
            pf.EmbeddedRoute += OnDockProfileRoute;         // instead of DialogResult + Close
            _dockBody.Controls.Add(pf);
            pf.Show();                                      // a non-top-level form still needs this to lay out
            _dockProfile = pf;
            _dockProfilePeerId = entry.PeerId;
        }

        private void DropDockProfile()
        {
            if (_dockProfile == null) return;
            try { _dockBody.Controls.Remove(_dockProfile); _dockProfile.Dispose(); } catch { }
            _dockProfile = null;
            _dockProfilePeerId = 0;
        }

        /// <summary>The embedded profile tapped something the HOST owns — a link, a mention, a hashtag, its
        /// personal-channel card, or it left the chat. Routed through the SAME RouteProfilePending the modal
        /// uses after ShowDialog, so the two modes cannot diverge. The pane does not close: ProfileForm
        /// clears its Pending* fields after this returns.</summary>
        private void OnDockProfileRoute(ProfileForm pf)
        {
            if (pf == null) return;
            RouteProfilePending(pf);
        }

        /// <summary>BATCH-TA-37 — the narrowest FORM width at which the dock may be open.
        /// 340 (dock) + 360 (Panel2MinSize, the conversation) + 240 (chat list) + chrome ≈ 960. Below it
        /// the dock does not merely look tight — it squeezes the conversation under its own minimum and the
        /// header controls collide, which is the overlap in the report.</summary>
        private const int DockMinFormWidth = 960;

        /// <summary>⚠ OPENING THE DOCK ON A NARROW WINDOW GROWS THE WINDOW; IT DOES NOT COVER THE CHAT.
        /// The dock is additional surface, so taking it out of the conversation's space is the one thing it
        /// must not do. If the window cannot grow — maximised, or the screen is too small — the dock is
        /// refused rather than opened on top of the chat.</summary>
        private bool EnsureRoomForDock()
        {
            if (Width >= DockMinFormWidth) return true;
            if (WindowState != FormWindowState.Normal) return false;   // maximised: nowhere to grow

            var wa = Screen.FromControl(this).WorkingArea;
            int want = Math.Min(DockMinFormWidth, wa.Width);
            if (want <= Width) return false;                            // screen itself is too narrow

            Width = want;
            if (Left + Width > wa.Right) Left = Math.Max(wa.Left, wa.Right - Width);
            return Width >= DockMinFormWidth;
        }

        /// <summary>The window got too narrow to hold the dock — close it rather than let it overlap.
        /// Called from the resize path, so dragging the edge in collapses the dock the way it grew.</summary>
        private void EnforceDockWidth()
        {
            if (_dock != null && _dock.Visible && Width < DockMinFormWidth) SetDockOpen(false);
        }

        private void ToggleDock()
        {
            bool want = _dock == null || !_dock.Visible;
            if (want && !EnsureRoomForDock())
            {
                if (Logger.Enabled) Logger.Diag("[DOCK] refused — window " + Width + "px < " + DockMinFormWidth);
                return;
            }
            SetDockOpen(want);
        }

        private void SetDockOpen(bool open)
        {
            if (_dock == null || _dock.Visible == open) return;
            _dock.Visible = open;
            if (open)
            {
                UpdateDockSources();      // may flip Emoji→Info if this chat can't be posted to
                // ⚠ THEN MATERIALISE THE PANE. This line is the fix for "a fresh run shows an empty Info
                //   pane until you visit Emoji or switch chats".
                //   UpdateDockSources only decides which TABS exist; it never builds pane CONTENT. And the
                //   two places that do build it had both already declined:
                //     · BuildDock ends with SetDockPane(Info), but at BuildUi time _selectedChat is null,
                //       so EnsureDockProfile drops and returns;
                //     · the chat-opened site only builds while the dock is VISIBLE, and on a fresh run it
                //       isn't open yet.
                //   So nothing had ever built the profile by the time the user first opened the dock.
                //   Re-applying the current pane is idempotent — EnsureDockProfile returns early when the
                //   peer is unchanged — and it routes through the one place that knows how to build either
                //   pane, rather than duplicating that decision here.
                SetDockPane(_dockPane);
            }
            if (_dockBtn != null) _dockBtn.Invalidate();   // the glyph carries the open/closed state
            if (Logger.Enabled)
                Logger.Diag("[DOCK] " + (open ? "opened" : "closed") + " w=" + _dock.Width
                            + " panel2=" + _split.Panel2.ClientSize.Width
                            + " chatArea=" + (_split.Panel2.ClientSize.Width - (open ? _dock.Width : 0)));
        }

        /// <summary>BATCH-TA-23/D1d — WHICH PANE SOURCES EXIST RIGHT NOW.
        ///
        /// ⚠ THIS SUBSCRIBES TO THE COMPOSER STATE, IT DOES NOT RECOMPUTE "can I post here".
        /// Core/ComposerState.cs is already the pure resolver for that, with a documented precedence
        /// (Blocked &gt; Join &gt; MuteUnmute &gt; BotStart &gt; Restricted), and <c>_footerKind</c> holds
        /// its answer. Re-deriving the rule here is how two surfaces end up disagreeing — the exact bug
        /// class TA-20/S0 exists to prevent.
        /// ⚠ CALLED FROM BOTH PLACES THAT ASSIGN _footerKind, so it tracks a LIVE change — a mute, an
        /// unblock, a slow-mode expiry — with no chat switch. Sampling it once at open would be wrong.
        /// ⚠ AN UNAVAILABLE SOURCE IS OMITTED, NOT GREYED: the Emoji tab disappears and Info fills the
        /// strip, matching AddMenuItem's convention and the Flip-button precedent in RoundRecorderForm.</summary>
        private void UpdateDockSources()
        {
            if (_dockEmojiTab == null) return;
            bool emojiOk = _selectedChat != null && _footerKind == ComposerKind.Compose;
            if (_dockEmojiTab.Visible != emojiOk) _dockEmojiTab.Visible = emojiOk;
            if (!emojiOk && _dockPane == DockPane.Emoji) SetDockPane(DockPane.Info);
            LayoutDockTabs();
        }

    }
}
