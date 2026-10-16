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
        private const int WM_ACTIVATEAPP = 0x001C;   // sent when the APPLICATION (not just a form) gains/loses foreground
        private bool _appActive = true;
        private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;   // OS accent/colorization changed (fires on 8.1 AND 10/11)

        /// <summary>TA-37 — the dock closes itself when the window is dragged narrower than it can hold.
        /// ⚠ Symmetric with EnsureRoomForDock: opening it GROWS the window, so shrinking must CLOSE it.
        /// Without this the dock stays open as the edge comes in and re-creates the overlap the threshold
        /// exists to prevent.</summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            EnforceDockWidth();
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            // BATCH-TA-28/T2 — the shell has (re)created our taskbar button. Anything we sent to
            // SetOverlayIcon before this point was silently discarded, and this fires AGAIN after an
            // explorer.exe restart — so without re-applying here the badge disappears for the rest of the
            // session the first time explorer crashes or is restarted.
            if (m.Msg == ShellNotify.WM_TaskbarButtonCreated)
            {
                ShellNotify.ReapplyTaskbarBadge(this);
                base.WndProc(ref m);
                return;
            }
            if (m.Msg == WM_ACTIVATEAPP)
            {
                bool active = m.WParam != IntPtr.Zero;   // wParam != 0 → app activated; 0 → app backgrounded
                if (!active && InputPolicy.ActivationGuard && IsSpuriousDeactivation())   // device trip: the guard has an off switch
                {
                    // FIX 1: the WinRT/touch keyboard is a SEPARATE surface that steals process activation while
                    // we're still the real foreground app + a text field is focused → this is NOT a background.
                    // Do NOT pause the WebM animation / churn — that pause+resume on every steal was the flicker.
                    if (LogOn) Logger.Diag("[KBD] WM_ACTIVATEAPP active=False → IGNORED (keyboard overlay, still foreground) fg=" + FgClass() + " composerFocus=" + (_messageInput != null && _messageInput.ContainsFocus));
                }
                else
                {
                    if (LogOn) Logger.Diag("[KBD] WM_ACTIVATEAPP active=" + active);
                    if (active != _appActive)
                    {
                        _appActive = active;
                        SetAnimationPaused(!active);   // real fg/bg change → pause/resume as before
                        // PRESENCE-TUNE A.1/A.2: focus-aware — activation sends online NOW (≤25s throttle);
                        // deactivation arms the 5s offline grace (alt-tab flap protection).
                        if (active) NoteActivity();
                        else _bgSinceTick = Environment.TickCount;
                    }
                }
            }
            else if (m.Msg == 0x0006)   // WM_ACTIVATE — 0=inactive 1=active(non-click) 2=clickactive
            {
                long wa = m.WParam.ToInt64() & 0xFFFF;
                if (LogOn) Logger.Diag("[KBD] WM_ACTIVATE " + wa);
                // WA_ACTIVE (1, NOT a mouse click): reactivated by the keyboard-close returning activation / alt-tab /
                // startup → WinForms auto-restores focus to the last text field, and Windows then auto-RESHOWS the
                // touch keyboard for it (the close→reshow loop). Clear that auto-focus so ONLY a user TAP (which
                // comes through WA_CLICKACTIVE / a direct hit, never WA_ACTIVE) opens the keyboard.
                if (wa == 1 && InputPolicy.RefocusClear) try { BeginInvoke((Action)ClearAutoRefocusedTextField); } catch { }
            }
            else if (m.Msg == 0x0086) { if (LogOn) Logger.Diag("[KBD] WM_NCACTIVATE active=" + (m.WParam != IntPtr.Zero)); }
            else if (m.Msg == WM_DWMCOLORIZATIONCOLORCHANGED)
            {
                // Windows accent / colorization changed (fires on 8.1 AND 10/11). Re-read the CHOSEN accent from
                // the registry via ThemeHelper (NOT m.WParam — that carries the composed colorization color, not the
                // picked swatch) and recolor every themed surface live, debounced. No restart.
                ThemeHelper.NotifyAccentChanged();
            }
            else if (m.Msg == TelegArm.Program.WM_ShowExisting && TelegArm.Program.WM_ShowExisting != 0)
            {
                // SINGLE-INSTANCE: a second launch broadcast this (HWND_BROADCAST) asking us to surface. Reuse the
                // tray-restore path (Show + un-minimize + Activate + BringToFront) — flicker-safe, and the second
                // instance already called AllowSetForegroundWindow(ASFW_ANY) so we're permitted to steal foreground.
                try
                {
                    if (!Visible || WindowState == FormWindowState.Minimized) RestoreFromTray();
                    else { Activate(); BringToFront(); }
                }
                catch { }
            }
            else if (m.Msg == 0x001A)   // WM_SETTINGCHANGE — KBD-CLOSE-PROBE: TabTip/tablet-mode broadcasts land here
            {
                if (LogOn)
                {
                    string area = "";
                    try { if (m.LParam != IntPtr.Zero) area = System.Runtime.InteropServices.Marshal.PtrToStringAuto(m.LParam) ?? ""; } catch { }
                    Logger.Diag("[KBD] WM_SETTINGCHANGE wParam=0x" + m.WParam.ToInt64().ToString("X") + " area=" + area + " fg=" + FgClass());
                }
            }
            base.WndProc(ref m);
        }

        // ── Tray icon + notifications ────────────────────────────────────────────

        /// <summary>Creates the tray icon (idempotent). Called once the session is authorized.</summary>
        private void SetupTray()
        {
            if (_notifyIcon != null) return;

            _trayIconNormal = TryLoadIcon("trayicon.ico");           // distinct from official Telegram; deployed beside the exe
            _trayIconUnread = TryLoadIcon("trayicon-unread.ico");    // shown while any chat has unread

            _trayMenu = new ThemedContextMenuStrip();
            AddMenuItem(_trayMenu, "Open TelegArm", RestoreFromTray);
            _trayMenu.Items.Add(new ToolStripSeparator());
            AddMenuItem(_trayMenu, "Exit", ExitApp);

            _notifyIcon = new NotifyIcon
            {
                Icon = _trayIconNormal ?? Icon ?? SystemIcons.Application,
                Text = "TelegArm",
                Visible = true,
                ContextMenuStrip = _trayMenu   // Windows' NATIVE tray right-click trigger — reliable (the manual MouseUp+Show showed nothing)
            };
            // The only NotifyIcon+ContextMenuStrip quirk is foreground: while the main window is VISIBLE the
            // menu could misbehave (the native path foregrounds the NotifyIcon's invisible helper window, which
            // doesn't hold activation). Correct it WITHOUT losing the native trigger — foreground the app just
            // before the menu opens. No-op when hidden (SFW can't foreground an invisible window), where the
            // native path already worked. Worst case: back to the old behavior (works hidden/minimized), never broken.
            _trayMenu.Opening += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[TRAY] menu opening");
                try { SetForegroundWindow(Handle); } catch { }
            };
            // Tray diagnostics + touch fixes (COMPOSER-CLOSEOUT): left-tap = one-directional restore (never hides,
            // so an accidental tap can't lose the window); right-click menu foregrounds ITSELF once shown so the
            // first touch tap lands on the menu instead of flying to the desktop (RT log: item taps died
            // reason=AppFocusChange with fg-class=Progman ~1s after open).
            _notifyIcon.MouseUp += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[TRAY] icon click button=" + e.Button + " windowVisible=" + Visible);
                if (e.Button == MouseButtons.Left)
                {
                    System.Diagnostics.Debug.WriteLine("[TRAY] left-tap restore");
                    try
                    {
                        if (!Visible || WindowState == FormWindowState.Minimized) RestoreFromTray();
                        else { Activate(); BringToFront(); }
                    }
                    catch { }
                }
            };
            _trayMenu.Opened += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[TRAY] menu opened");
                try { SetForegroundWindow(_trayMenu.Handle); } catch { }   // the MENU's handle — not the form's
            };
            _trayMenu.Closed += (s, e) => System.Diagnostics.Debug.WriteLine("[TRAY] menu closed reason=" + e.CloseReason);
            _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();
            _notifyIcon.BalloonTipClicked += OnBalloonClicked;
            UpdateTrayTooltip();
        }

        /// <summary>Hides the window to the tray (falls back to minimize if there's no tray yet).</summary>
        private void HideToTray()
        {
            if (_notifyIcon == null) { WindowState = FormWindowState.Minimized; return; }
            SaveWindowPlacement();   // R1: the process may end in the tray (logoff, shutdown) without another chance
            Hide();
        }

        private void RestoreFromTray()
        {
            ShowInTaskbar = true;   // STARTUP-SETTING: a --startup launch began off-taskbar; ensure the taskbar button returns
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = RestoreState;   // R1: back maximised if it was
            Activate();
            BringToFront();
        }

        // ── R1: window placement ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Redesign R1 — records the window's NORMAL bounds (also while maximised, minimised or in the tray) and
        /// whether it was maximised. ⚠ Never the touch-keyboard-shrunk rectangle: while that shrink is active the bounds
        /// to keep are the ones saved before it (MainForm.Keyboard's _savedBounds).</summary>
        private void SaveWindowPlacement()
        {
            try
            {
                if (!IsHandleCreated || IsDisposed) return;
                Rectangle normal;
                bool max;
                if (_kbShrinkActive) { normal = _savedBounds; max = _savedState == FormWindowState.Maximized; }
                else
                {
                    normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                    max = (WindowState == FormWindowState.Minimized ? RestoreState : WindowState) == FormWindowState.Maximized;
                }
                if (normal.Width <= 0 || normal.Height <= 0) return;
                int dpi = Math.Max(48, WindowDpiValue);
                var s = AppSettings.Instance;
                s.MainWindow = new WindowPlacementSettings
                {
                    X = normal.X, Y = normal.Y,
                    Width = (int)Math.Round(normal.Width * 96.0 / dpi),
                    Height = (int)Math.Round(normal.Height * 96.0 / dpi),
                    Maximized = max,
                };
                s.Save();
                if (LogOn) Logger.Diag("[PLACEMENT] saved " + normal.X + "," + normal.Y + " " + s.MainWindow.Width + "x" + s.MainWindow.Height
                                       + " (logical) dpi=" + dpi + " max=" + max);
            }
            catch (Exception ex) { Logger.Diag("[PLACEMENT] save failed: " + ex.Message); }
        }

        /// <summary>Redesign R1 — puts the window where it was, at its saved logical size in this window's pixels, if a
        /// working area still holds its title bar (else: centred at the default size, as before). Called from the
        /// constructor, before the first show.</summary>
        private void ApplySavedPlacement()
        {
            try
            {
                var p = AppSettings.Instance.MainWindow;
                if (p == null || p.Width <= 0 || p.Height <= 0) return;
                var size = new Size(Math.Max(Px(p.Width), Px(LogicalMinimumSize.Width)), Math.Max(Px(p.Height), Px(LogicalMinimumSize.Height)));
                var areas = Array.ConvertAll(Screen.AllScreens, sc => sc.WorkingArea);
                var fit = WindowPlacementMath.Fit(new Point(p.X, p.Y), size, areas, Px(40));
                if (fit == null) { Logger.Diag("[PLACEMENT] saved position is off every screen; centring"); return; }
                StartPosition = FormStartPosition.Manual;
                Bounds = fit.Value;
                if (p.Maximized) { WindowState = FormWindowState.Maximized; RestoreState = FormWindowState.Maximized; }
                if (LogOn) Logger.Diag("[PLACEMENT] restored " + fit.Value + " max=" + p.Maximized);
            }
            catch (Exception ex) { Logger.Diag("[PLACEMENT] restore failed: " + ex.Message); }
        }

        private void ExitApp()
        {
            _reallyClosing = true;
            Close();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // A user-initiated close (X / Alt+F4), once the tray exists and we're not genuinely closing (Exit/logout), does
            // what Settings > General > "When I close the window" says (step 2.7c, plan D8): minimise to the tray (the
            // default, 1.x behaviour), ask, or exit.
            if (!_reallyClosing && _notifyIcon != null && e.CloseReason == CloseReason.UserClosing)
            {
                var action = CloseBehaviorSetting.Parse(AppSettings.Instance.CloseBehavior);
                if (action == CloseAction.Ask)
                {
                    using (var d = new CloseChoiceDialog())
                    {
                        if (d.ShowDialog(this) != DialogResult.OK) { e.Cancel = true; return; }   // Esc / X: neither
                        action = d.Choice;
                        if (d.Remember)
                        {
                            AppSettings.Instance.CloseBehavior = CloseBehaviorSetting.Format(action);
                            AppSettings.Instance.Save();
                        }
                    }
                }
                if (action != CloseAction.Exit)
                {
                    e.Cancel = true;
                    HideToTray();
                    return;   // tray-hide = backgrounded; the presence timer sends offline after 30s
                }
                _reallyClosing = true;   // Exit: fall through to the real close below
            }
            SaveWindowPlacement();   // R1: a real close
            CloseReactionGrid();     // T12: an unowned popup must not outlive the form (see the field's note)
            // DRAFTS: best-effort save of the open chat's draft on a REAL close (bounded like presence; never blocks
            // shutdown). The primary save is on chat/account switch — this covers "typed then closed without switching".
            try
            {
                var dchat = _selectedChat;
                if (dchat != null && _thread == null && _editTarget == null && _messageInput != null && !TelegramService.TearingDown)
                {
                    string dtext = (CurrentComposerText() ?? "").Trim();   // 2.7d: the editor window's text if it is open
                    if (dtext != (dchat.DraftText ?? "")) _service?.SaveDraftAsync(dchat.Peer, dtext)?.Wait(600);
                }
            }
            catch { }
            // PRESENCE 1.2: best-effort offline on a REAL close — bounded wait, never blocks
            // shutdown (800ms cap) and never fights teardown (_tearingDown discipline).
            if (_presenceOnline && !TelegramService.TearingDown)
            {
                _presenceOnline = false;
                try { _service?.UpdateStatusAsync(true).Wait(800); } catch { }
                if (LogOn) System.Diagnostics.Debug.WriteLine("[PRESENCE] sent offline (app close)");
            }
        }

        /// <summary>Updates the tray tooltip with the total unread count — and, since TA-33, the taskbar
        /// icon badge and the Start tile, which are the same fact shown in two more places.</summary>
        private void UpdateTrayTooltip()
        {
            // ⚠ THE TRAY GUARD MOVED DOWN (TA-33). It used to be the first line, which would have made the
            //   TASKBAR badge depend on a tray icon it has nothing to do with — the same shape of mistake
            //   TA-27 fixed in the notify gate. The unread total is computed unconditionally now, and only
            //   the tray-specific lines stay behind the guard.
            // ⚠ TWO TOTALS, AND THEY ARE DIFFERENT ON PURPOSE (BATCH-TA-28/T4).
            //   `unread`        — every unread message. What the tray tooltip has always shown; unchanged.
            //   `unreadLoud`    — unread in chats the user has NOT muted. What the BADGE and the TILE show.
            // A badge is a demand for attention, so counting chats the user explicitly silenced would be
            // the badge arguing with a decision they already made — the same reasoning as the notify gate.
            // ⚠ THE RESOLVER IS ComputeEffectiveMuted, the one the ROW BELL ICON uses, deliberately. The
            //   notify gate uses TelegramService.IsPeerEffectivelyMuted, and TA-26 kept both on purpose
            //   (that one fails CLOSED for a gate; this one reads the UI's own list). For a COUNT the right
            //   answer is the one the user can SEE: a badge that disagrees with the visible bell icons is
            //   the actual bug. Same resolver as the tile, so those two can never disagree either.
            // ⚠ AND THE NUMBER IS STILL ONLY AS COMPLETE AS `_allChats` (TA-28/T1) — see UpdateStartTile.
            int unread = 0, unreadLoud = 0;
            foreach (var c in _allChats)
            {
                int n = Math.Max(0, c.UnreadCount);
                if (n == 0) continue;
                unread += n;
                if (!ComputeEffectiveMuted(c)) unreadLoud += n;
            }

            // ── TA-33 — THE BADGE ON THE ICON ───────────────────────────────────────────────────
            // ⚠ DRIVEN FROM THE UNREAD TOTAL, **NOT** FROM THE NOTIFY EMIT PATH, AND THAT IS DELIBERATE.
            //   A badge is AMBIENT — it states what is unread, which stays true whether or not a
            //   notification was allowed to interrupt. Hanging it off the emit point would mean master
            //   mute, a muted chat, a `silent` message or a suppressed backlog all silently desynced the
            //   badge from reality, and the user would find a count that disagrees with the chat list.
            //   This is the same "do not interrupt me" vs "do not tell me" line N4 draws.
            ShellNotify.SetTaskbarBadge(this, unreadLoud);   // RT 8.1 / Win7 / Win11 — no AUMID needed
            ShellNotify.SetTileBadge(unreadLoud);            // Start tile only; no-op without a shortcut
            UpdateStartTile(unreadLoud);

            if (_notifyIcon == null) return;
            _notifyIcon.Text = unread > 0 ? "TelegArm — " + unread + " unread" : "TelegArm";
            // Swap the tray icon on the 0↔nonzero boundary (unread → unread icon; none → normal). Only assign
            // when it changes (avoids redundant handle churn). Falls back gracefully if an icon is missing.
            var want = unread > 0 ? (_trayIconUnread ?? _trayIconNormal) : _trayIconNormal;
            if (want != null && !ReferenceEquals(_notifyIcon.Icon, want)) _notifyIcon.Icon = want;
        }

        /// <summary>BATCH-TA-33/N6 — the Start tile: up to 5 unread chats, cycled by Windows.
        ///
        /// ★ L5, THE RULE: **MASTER MUTE DOES NOT CLEAR OR FREEZE THE TILE.** A tile is AMBIENT — you go
        ///   and look at it — where a notification is an INTERRUPTION that comes to you. Silencing
        ///   interruptions is not a request to be denied information you deliberately went to check.
        /// ⚠ THIS IS WHY THE TILE IS DRIVEN FROM THE UNREAD RECOMPUTE RATHER THAN FROM THE EMIT POINT, AS
        ///   N6/L4 SUGGESTED. The emit point sits DOWNSTREAM of master mute (N4), so hanging the tile there
        ///   would have frozen it for the entire time the user was muted — which is precisely the "yes" to
        ///   L5 that L5 asked me not to give. Same source as the badge, one notion of "what is unread".
        /// ⚠ L4 IS STILL HONOURED WHERE IT MATTERS: a muted chat contributes nothing, because
        ///   ComputeEffectiveMuted is the same resolver the row bell uses.
        /// ⚠ L6 PRIVACY: with previews off, the tile shows senders only. It reuses
        ///   <see cref="AppSettings.NotificationPreviews"/> — the notification setting — rather than adding a
        ///   second switch, because "who may read my messages over my shoulder" is one question, and two
        ///   toggles that answer it differently is a privacy bug waiting to happen.</summary>
        private void UpdateStartTile(int unreadLoud)
        {
            if (!ShellNotify.Available) return;   // no tile surface / no shortcut → nothing to do
            try
            {
                if (unreadLoud <= 0) { ShellNotify.ClearTile(); return; }
                bool previews = AppSettings.Instance.NotificationPreviews;
                var items = new List<Tuple<string, string>>();
                foreach (var c in _allChats)
                {
                    if (items.Count >= 5) break;
                    if (c.UnreadCount <= 0) continue;
                    if (ComputeEffectiveMuted(c)) continue;             // L4: muted chats contribute nothing
                    string who = c.Title ?? "Chat";
                    string what = previews
                        ? (c.Preview ?? "")
                        : (c.UnreadCount == 1 ? "1 new message" : c.UnreadCount + " new messages");
                    items.Add(Tuple.Create(who, what));
                }
                if (items.Count == 0) ShellNotify.ClearTile(); else ShellNotify.PushTile(items);
            }
            catch { /* the tile is decoration; it never breaks the app */ }
        }

        /// <summary>Loads an .ico from beside the exe (RT-safe: null if missing/unreadable, never throws).</summary>
        private static Icon TryLoadIcon(string fileName)
        {
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                // Redesign 1.8: ask for the tray's own size (SM_CXSMICON: 16/20/24 px at 100/125/150%) — the .ico has every
                // one of those frames now, so Windows no longer stretches a 32 px frame down (or a 16 px one up).
                if (System.IO.File.Exists(path)) return new Icon(path, SystemInformation.SmallIconSize);
            }
            catch { }
            return null;
        }

        // NOTIFY-FIX 2.2: a message that reached the gate once is DECIDED — re-delivery (getDifference
        // replay on the flappy VPN link, stale-state re-attach) must never re-decide it. FIFO-capped.
        private readonly HashSet<(long, int)> _toastSeen = new HashSet<(long, int)>();
        private readonly Queue<(long, int)> _toastSeenOrder = new Queue<(long, int)>();
        private const int ToastSeenCap = 500;
        // NOTIFY-BACKGROUND: account-scoped dedup ((account, peer, msg)) so two accounts in the SAME group both notify.
        private readonly HashSet<(long, long, int)> _bgToastSeen = new HashSet<(long, long, int)>();
        private readonly Queue<(long, long, int)> _bgToastSeenOrder = new Queue<(long, long, int)>();

        // ── BATCH-TA-26/B1 — THE BACKLOG GATE ────────────────────────────────────────────────────
        /// <summary>How far BEFORE this process started a message may be dated and still notify.
        /// Covers the ordinary race where a message is sent while we are connecting: the update can be
        /// delivered a few seconds after start-up while its server timestamp predates it. 60 s is generous
        /// for that and still far below any real "I was offline" gap.</summary>
        private const int NotifyBacklogGraceSeconds = 60;
        private static readonly DateTime NotifyProcessStartUtc = DateTime.UtcNow;

        /// <summary>TRUE for a message that predates this run — i.e. one replayed by getDifference rather
        /// than newly received.
        ///
        /// ⚠ WHY A DATE AND NOT A "was this live?" FLAG: there isn't one. WTC's UpdateManager SYNTHESISES
        /// UpdateNewMessage from the getDifference result and raises it through the SAME callback as a live
        /// update (HandleDifference), so nothing on the update distinguishes them. The message's own
        /// server-set timestamp is the only honest signal we have.
        /// ⚠ AND WHY NO PERSISTED WATERMARK, which is the obvious "smarter" version: it re-creates the very
        /// bug this fixes. A watermark says "notify everything since I last looked", so the first launch
        /// after a few hours offline would fire a burst of toasts — exactly the behaviour being removed.
        /// What the user missed is already carried, better, by the unread badges and the chat list; a
        /// notification is an INTERRUPTION, and interrupting about something that happened hours ago is
        /// noise. Deliberate: launching does not re-announce the backlog.
        /// The message pump only starts after start-up, so `_toastSeen` cannot help here — on a cold start
        /// it is empty, which is precisely why every backlogged message used to get through.</summary>
        private static bool IsBacklog(Message m)
        {
            return m != null && m.date < NotifyProcessStartUtc.AddSeconds(-NotifyBacklogGraceSeconds);
        }

        // ── BATCH-TA-26c — DOES THIS MESSAGE MENTION *ME*? ───────────────────────────────────────
        /// <summary>The mute break-through test. Answers <c>how</c> with the rule that fired, so the log
        /// says WHY a message got through instead of leaving it to be re-derived later.
        ///
        /// ⚠ WHY THIS IS NOT JUST `Message.Flags.mentioned`, WHICH IS WHAT WE USED TO DO.
        /// MEASURED (log telegarm_20260806_125552.log, peer 1824808427 — a channel muted only by the
        /// account-level broadcasts default):
        ///     id=90 preview='@xhamedz'  -> [NOTIFY] suppressed … reason=muted
        ///     id=92 preview='@xhamedz'  -> [NOTIFY] suppressed … reason=muted
        /// `@xhamedz` IS the receiving account's username, and the server did NOT set the flag — the gate
        /// only reaches the mute check when the flag is false. In the SAME chat in an earlier run, replies
        /// DID carry it (msgs 86/88 emitted), so the server sets it for replies-to-me and not for this.
        /// Official clients show these notifications because they do not trust one bit: they decide
        /// mention-ness themselves from the entities and the user's own identity. So do we now.
        ///
        /// FOUR RULES, cheapest first, each recorded distinctly so the next log is self-explaining:
        ///   flag        — the server said so (still authoritative; covers replies-to-me)
        ///   entity-id   — MessageEntityMentionName addressed at my user id (a text-mention of me by name)
        ///   entity-text — MessageEntityMention whose text is my @username
        ///   text        — my @username appears in the body with NO entity at all. This is the one that
        ///                 catches the case above, and it is deliberate: a message that writes your handle
        ///                 is addressing you whether or not the server chose to encode an entity.
        /// ⚠ Rule 4 is token-exact: "@hamed" must not match "@hamedz". Without that guard every user whose
        ///   username is a prefix of someone else's would break through on the wrong messages.</summary>
        private static bool MentionsMe(Message m, User me, out string how)
        {
            how = null;
            if (m == null) return false;
            if ((m.flags & Message.Flags.mentioned) != 0) { how = "flag"; return true; }
            if (me == null) return false;

            string text = m.message ?? "";
            if (m.entities != null)
                foreach (var e in m.entities)
                {
                    if (e is MessageEntityMentionName mn && mn.user_id == me.id) { how = "entity-id"; return true; }
                    if (e is InputMessageEntityMentionName imn && imn.user_id is InputUser iu && iu.user_id == me.id)
                    { how = "entity-id"; return true; }
                }

            string un = me.MainUsername;
            if (string.IsNullOrEmpty(un)) return false;

            if (m.entities != null)
                foreach (var e in m.entities)
                    if (e is MessageEntityMention && e.offset >= 0 && e.length > 0
                        && e.offset + e.length <= text.Length
                        && string.Equals(text.Substring(e.offset, e.length).TrimStart('@'), un,
                                         StringComparison.OrdinalIgnoreCase))
                    { how = "entity-text"; return true; }

            if (ContainsUsernameToken(text, un)) { how = "text"; return true; }
            return false;
        }

        /// <summary>"@name" present as a WHOLE token — the next character must not continue a username
        /// (Telegram usernames are letters, digits and underscore), so "@hamed" does not match "@hamedz".</summary>
        private static bool ContainsUsernameToken(string text, string username)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(username)) return false;
            string needle = "@" + username;
            int i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = i + needle.Length;
                if (end >= text.Length) return true;
                char c = text[end];
                if (!(char.IsLetterOrDigit(c) || c == '_')) return true;
                i = end;
            }
            return false;
        }

        /// <summary>One [NOTIFY] line per gate decision (Logger.Enabled-gated; nothing per tick).</summary>
        private static void NotifyLog(string what, long peerId, int msgId, string reason)
        {
            if (!Logger.Enabled) return;   // pre-gate: skip the string building entirely when logging is off
            Logger.Diag("[NOTIFY] " + what + " peer=" + peerId + " msg=" + msgId
                + (reason != null ? " reason=" + reason : ""));
        }

        /// <summary>THE notify gate: own → dup → focused → mute (explicit, then category), then emit.
        /// Every decision is logged; the (peer,msg) key is recorded on FIRST sight so a replayed message
        /// can never notify twice in a process lifetime (NOTIFY-FIX).</summary>
        private void MaybeToast(long peerId, Message m, bool outgoing)
        {
            if (m == null) return;
            if (!CanDeliverNotification()) return;                                 // TA-27/W7: the tray guard, split

            // ── BATCH-TA-32/N4 — THE MASTER MUTE ─────────────────────────────────────────────────
            // ⚠ M1 — IT GATES THE **EMIT PATH ONLY**. The unread badge, the tray icon and tooltip, and
            //   the chat-list preview are all updated by UpdateChatListForMessage / UpdateTrayTooltip
            //   BEFORE this method is ever called, on a path that does not run through here at all. Master
            //   mute means "do not INTERRUPT me", not "hide my messages" — the same distinction TA-26a
            //   drew for Message.Flags.silent. DO NOT widen this to skip the badge, the tray or the list;
            //   a user who silences notifications still expects to see what arrived when they look.
            //
            // ★ M4 — THE RULE, DECIDED: **MASTER MUTE WINS OVER EVERYTHING, MENTIONS INCLUDED.**
            //   A mention breaks through a PER-CHAT mute because that mute says "not this conversation",
            //   and being addressed by name is the documented exception to it (TA-26c). The master switch
            //   is a different statement — "nothing, from anywhere, right now" — and an exception to it
            //   would make it not a master switch. If the user wants mentions only, that is a quiet-hours
            //   or per-category feature, not a hole in the global off.
            //   This is also the EXISTING behaviour, preserved deliberately: the check sits ABOVE the
            //   MentionsMe call below, so a mention has never broken through it. TA-32 documents and logs
            //   that ordering rather than changing it.
            //
            // ⚠ M3 — AND IT IS LOGGED. It used to say "(user choice, not logged)" and return silently,
            //   which is precisely how "notifications stopped working" becomes an unanswerable bug report:
            //   every OTHER gate in this method records a reason, so a log with no [NOTIFY] line at all
            //   looked like the update never arrived rather than like a switch being off.
            //   Note it fires per invocation, and MaybeToast is invoked up to 7x per message (§2f), so a
            //   muted run is chatty — acceptable, because it is Logger.Enabled-gated and off by default.
            if (!AppSettings.Instance.EnableNotifications)
            { NotifyLog("suppressed", peerId, m.ID, "master"); return; }

            if (outgoing) { NotifyLog("suppressed", peerId, m.ID, "own"); return; }

            // ⚠ BATCH-TA-26a/S1 — THE SENDER ASKED FOR NO PING. `silent` is Telegram's "send without
            //   sound/notification" (WTC: "whether this is a silent message (no notification triggered)"),
            //   set by the sender or by a channel posting silently. We never checked it, so we pinged for
            //   messages explicitly marked not to.
            //   ⚠ THIS SUPPRESSES THE **NOTIFICATION ONLY**. The message is still delivered, still lands in
            //     the chat, and STILL COUNTS TOWARD THE UNREAD BADGE — silent means "don't interrupt me",
            //     not "hide it". Do not "fix" this into skipping the badge or the chat-list preview.
            if ((m.flags & Message.Flags.silent) != 0)
            { NotifyLog("suppressed", peerId, m.ID, "silent"); return; }

            if (IsBacklog(m)) { NotifyLog("suppressed", peerId, m.ID, "backlog"); return; }

            var key = (peerId, m.ID);
            if (_toastSeen.Contains(key)) { NotifyLog("suppressed", peerId, m.ID, "dup"); return; }
            _toastSeen.Add(key);
            _toastSeenOrder.Enqueue(key);
            while (_toastSeenOrder.Count > ToastSeenCap) _toastSeen.Remove(_toastSeenOrder.Dequeue());

            if (_isForeground && _selectedChat != null && _selectedChat.PeerId == peerId)
            { NotifyLog("suppressed", peerId, m.ID, "focused"); return; }

            // MENTION-REACTION: break-through-mute. A message flagged `mentioned` NOTIFIES EVEN in a muted
            // chat (client-side rule; Telegram has no server mention-mute). Unchanged by TA-26.
            // ⚠ WHAT `mentioned` ACTUALLY COVERS — **OBSERVED**, not read off a doc (TA-26b/D3).
            //   *** A REPLY TO YOUR OWN MESSAGE SETS THIS FLAG. *** Measured on a real account, log
            //   telegarm_20260806_124156.log, peer 1824808427 — a chat whose OTHER messages the resolver
            //   reported muted in the same run:
            //       12:43:52.121  [NOTIFY] emit      peer=1824808427 msg=86
            //       12:44:00.479  [NOTIFY] suppressed peer=1824808427 msg=87 reason=muted
            //       12:44:23.202  [NOTIFY] emit      peer=1824808427 msg=88
            //       12:44:27.886  [NOTIFY] suppressed peer=1824808427 msg=89 reason=muted
            //   86 and 88 were REPLIES and they got through; the only route past the mute gate is this
            //   flag, so the server had set it on both.
            //   ⚠ I PREVIOUSLY NARROWED THIS TO "@mentions and text-mentions, replies not documented",
            //     reasoning from WTC's summary and Telegram's mentions page. THAT WAS WRONG, and it is a
            //     standing reminder that the docs describe the entity case without excluding the reply
            //     case — absence of a statement is not a statement of absence. The log is the authority.
            //   ⚠ SEPARATE, STILL UNEXPLAINED: in that same run a message the tester INTENDED as an
            //     @mention did NOT carry the flag (it is one of the two suppressed as `muted`). The gate
            //     behaved correctly — it can only read the flag the server set — so this is a question
            //     about how the mention was composed (does the recipient account actually have that
            //     username?), NOT about this code. Do not "fix" the gate for it.
            string mentionHow;
            bool mentioned = MentionsMe(m, _service != null ? _service.Me : null, out mentionHow);

            // ⚠ BATCH-TA-26/B2 — THE MUTE ANSWER COMES FROM THE SERVICE, NOT FROM `_allChats`.
            // This used to be `_allChats.FirstOrDefault(...)` fed into IsEffectivelyMuted, whose first line
            // was `if (entry == null) return false;  // unknown chat → can't be muted`. `_allChats` holds ONE
            // page of ~100 dialogs until the list is scrolled (TA-11/TA-14), so a muted chat further down was
            // simply NOT FOUND, the gate answered "not muted", and it notified. That is the reported bug.
            // The BACKGROUND path never had it: it already asks svc.IsPeerEffectivelyMuted, which resolves
            // live per-peer override → dialog snapshot → category default and touches no UI list at all.
            // Both paths now share that one resolver, and it FAILS CLOSED (unknown ⇒ silent).
            if (!mentioned)
            {
                var svc = _service;
                if (svc == null) { NotifyLog("suppressed", peerId, m.ID, "no-service"); return; }   // fail closed
                if (svc.IsPeerEffectivelyMuted(m.peer_id))
                {
                    // `reply=1` is diagnostic only (TA-26c): if a REPLY to us is ever suppressed here, the
                    // server did not set the flag for it either and the break-through needs a reply rule
                    // as well as the mention rules. Nothing branches on it.
                    NotifyLog("suppressed", peerId, m.ID, "muted" + (m.reply_to != null ? " reply=1" : ""));
                    return;
                }
            }

            // `_allChats` is still consulted for the TITLE — that is presentation, and being wrong there
            // costs a generic caption, not a wrongly-delivered notification.
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);

            // ⚠ BATCH-TA-26b/D4 — RECORD *WHY* IT NOTIFIED, not just that it did.
            //   The gate logged every SUPPRESSION with a reason but emitted anonymously, so "why did this
            //   notify?" was unanswerable from a log — the exact mirror of the bug that started this whole
            //   thread, where a silent drop had no reason either. `mention` means it broke THROUGH a mute;
            //   `not-muted` means the mute gate simply said no. Those two are indistinguishable in the
            //   output otherwise, and telling them apart is what settles whether `mentioned` is set for
            //   replies (TA-26b/D3 could not answer that from the previous logs).
            NotifyLog("emit", peerId, m.ID, mentioned ? "mention:" + mentionHow : "not-muted");
            EmitNotification(BuildNotification(AccountContext.ActiveId, peerId, m,
                                               entry?.Title ?? "TelegArm", null, mentioned));
        }

        // ── BATCH-TA-27 — ONE CONSTRUCTION, ONE EMITTER, TWO CHANNELS ────────────────────────────
        /// <summary>W6 — composes the notification BOTH channels show. Previously the active and
        /// background paths each built their own caption and each carried their own copy of the
        /// "empty ⇒ 'New message', over 160 ⇒ truncate" rules; the window would have made that three.
        /// The ONLY difference between the two callers is <paramref name="accountName"/>.</summary>
        private NotifyInfo BuildNotification(long acctId, long peerId, Message m,
                                             string chatTitle, string accountName, bool mentioned)
        {
            if (string.IsNullOrEmpty(chatTitle)) chatTitle = "TelegArm";
            if (mentioned) chatTitle = "@ " + chatTitle;          // distinguish a mention
            // ⚠ TA-33/L6 — PRIVACY IS APPLIED **HERE**, at the one construction point, so the window, the
            //   Action Center entry and the legacy balloon cannot disagree about it. Applying it per
            //   surface is how one of them ends up leaking the body after the user turned previews off.
            //   (The tile reads the same flag from its own source — see UpdateStartTile.)
            string text = AppSettings.Instance.NotificationPreviews
                ? GetDisplayText(m)
                : "New message";
            if (string.IsNullOrEmpty(text)) text = "New message";
            if (text.Length > 160) text = text.Substring(0, 160) + "…";
            return new NotifyInfo
            {
                AccountId = acctId,
                PeerId = peerId,
                MessageId = m.ID,
                Title = accountName != null ? accountName + " · " + chatTitle : chatTitle,
                Text = text,
                // ⚠ CACHE ONLY — GetCachedAvatar is `_avatars.GetCached`, a pure lookup. The notification
                //   path must never fetch: it would make an already-late notification later, and fire
                //   network work from a path that can run while the app is otherwise idle. A miss just
                //   draws the initials circle, which is what the chat list does too.
                Avatar = GetCachedAvatar(peerId),
                AvatarPeerId = peerId
            };
        }

        /// <summary>W7 — deliver it. The window is the channel; the tray balloon is an EXCLUSIVE fallback
        /// behind <see cref="AppSettings.LegacyTrayBalloon"/> (default off) so a window that misbehaves on
        /// the RT device cannot leave notifications dead. Remove the balloon branch once the window is
        /// device-proven.</summary>
        private void EmitNotification(NotifyInfo info)
        {
            if (info == null) return;

            // ── BATCH-TA-33/A1 — THE ACTION CENTER ENTRY HANGS OFF *THIS* POINT, NOT A PATH OF ITS OWN ──
            // ⚠ AND IT MUST SIT **HERE**, ABOVE THE LegacyTrayBalloon BRANCH. That branch RETURNS (twice),
            //   so the same call appended at the end of this method would be silently skipped for every
            //   user who flipped the escape hatch on — the sort of half-working that only shows up on the
            //   one machine you cannot debug. This line is the last point both channels share.
            // ⚠ EVERY GATE HAS ALREADY RUN by the time we are here: master mute, own, silent, backlog, dup,
            //   focused, per-chat mute and the mention break-through are all upstream in MaybeToast /
            //   BackgroundToast. That is what makes this additive rather than a second surface — and it is
            //   why A7 needs no code: master mute returns long before this.
            //   (One honest exception: the fullscreen/user-busy gate lives DOWNSTREAM in
            //   NotificationStack.Show, so a game covering the screen suppresses the WINDOW but still gets
            //   an Action Center entry. That is the right way round — the entry is the quiet record you
            //   read afterwards, which is exactly what you want after being left alone.)
            ShellNotify.PushToast(info.Title, info.Text,
                                  info.MessageId.ToString(), info.PeerId.ToString(),
                                  ToastActivator.BuildArgs(info.AccountId, info.PeerId, info.MessageId));

            if (AppSettings.Instance.LegacyTrayBalloon)
            {
                if (_notifyIcon == null) return;                  // see CanDeliverNotification
                // ⚠ `_lastNotified*` IS THE BALLOON'S LIMITATION, NOT A SHARED MECHANISM. One tray icon can
                //   show one balloon, so one slot is all it can address. The window path deliberately does
                //   NOT touch these fields — each window carries its own (account, peer, message).
                _lastNotifiedPeerId = info.PeerId; _lastNotifiedAccountId = info.AccountId;
                try { _notifyIcon.ShowBalloonTip(4000, info.Title, info.Text, ToolTipIcon.None); }
                catch { /* tray gone */ }
                return;
            }
            NotificationStack.Show(this, info, _dark, _accent);
        }

        /// <summary>⚠ THE TRAY GUARD **SPLITS**, IT DOES NOT MOVE. Both notify paths used to open with
        /// `if (_notifyIcon == null) return;`, which is right for a balloon and wrong for a window — the
        /// window needs no tray at all, and leaving the guard in place would have made a missing tray
        /// silently disable the new channel. But simply LIFTING it is also wrong: the legacy balloon path
        /// would then dereference a null NotifyIcon. So the requirement becomes "is the channel we are
        /// actually going to use available?", which is this.</summary>
        private bool CanDeliverNotification()
        {
            return !AppSettings.Instance.LegacyTrayBalloon || _notifyIcon != null;
        }

        private void OnBalloonClicked(object sender, EventArgs e)
        {
            // The balloon can only ever be about the most recent notification — hence the shared slot.
            OpenNotifiedChat(_lastNotifiedAccountId, _lastNotifiedPeerId);
        }

        /// <summary>TA-27/W5 — a notification WINDOW was clicked. Same destination as a balloon click, but
        /// the identity comes from the window that was actually clicked rather than from a shared slot, so
        /// clicking the second of three notifications opens the second chat.</summary>
        private void OnNotificationActivated(long acctId, long peerId, int msgId)
        {
            OpenNotifiedChat(acctId, peerId);
        }

        /// <summary>TA-37/A2 — an Action Center entry was clicked. Windows calls the COM activator on an
        /// RPC thread, so hop to the UI thread and then take the SAME route a notification-window click
        /// takes. Nothing here knows how to open a chat; that stays in one place.</summary>
        private void OnToastActivated(long acctId, long peerId, int msgId)
        {
            try
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke((Action)(() => OnNotificationActivated(acctId, peerId, msgId)));
            }
            catch (Exception ex) { Logger.Diag("[SHELL] activation dispatch failed: " + ex.Message); }
        }

        /// <summary>The one implementation behind both click paths: restore the window, switch accounts if
        /// the notification belonged to a BACKGROUND account, then open the chat.</summary>
        private async void OpenNotifiedChat(long acctId, long peerId)
        {
            RestoreFromTray();          // the INTENTIONAL activation — see NotificationWindow's W1 block
            if (peerId == 0) return;
            // NOTIFY-BACKGROUND: a BACKGROUND account's notification → SWITCH to that account first (fast warm
            // rebind), THEN open the chat in the now-active account. An active-account one (acctId == active,
            // or 0) just opens the chat.
            if (acctId != 0 && acctId != AccountContext.ActiveId)
            {
                var acc = AccountStore.ListAccounts().FirstOrDefault(a => a.Id == acctId);
                await SwitchAccountAsync(acctId, acc != null ? acc.Name : null);
            }
            var entry = _allChats.FirstOrDefault(c => c.PeerId == peerId);
            if (entry != null && entry != _selectedChat) await OpenChat(entry, 0);
        }

    }
}
