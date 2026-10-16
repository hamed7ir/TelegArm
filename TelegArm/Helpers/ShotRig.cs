using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.UI;
using TL;
using Message = TL.Message;

namespace TelegArm.Helpers
{
    /// <summary>STEP 0.1 — the hidden Release switch <c>TelegArm.exe --shoot &lt;dir&gt;</c>. Builds each form in a
    /// fixed state, light then dark, and saves what it draws as PNGs, so the redesign can be diffed against a
    /// baseline (scripts/compare-shots.ps1) instead of judged by eye. Release, not DEBUG-only: the RT device
    /// only ever runs Release.
    ///
    /// How it stays safe and offline:
    ///   · Program.RunShoot isolates every writable root first (StoragePaths.UseIsolatedRoot), so the forms see
    ///     no accounts, no settings and an empty cache. Run() checks afterwards that no session file appeared.
    ///   · Forms are realised HIDDEN and never shown. Form.OnLoad is raised only once a form has been made
    ///     visible, so MainForm.OnLoad — the session resume, i.e. the network — never runs here, and LoginForm's
    ///     Shown handler (the QR login) never fires. ⚠ That is load-bearing: never "fix" a blank shot by Show().
    ///   · The demo chat goes through MainForm's own render path (RenderChatList, AddMessageBubble) with
    ///     hand-made TL objects, by reflection — the rig drives forms from outside and adds no hooks to them.
    ///     Reflection names are checked at run time: a renamed member fails that one shot loudly.
    ///
    /// Determinism: fixed dates in the past (no "Today"/"Yesterday"), no avatars (initials only), no caret
    /// (nothing has focus). Two runs on one machine must give identical PNGs; that is step 0.1's acceptance.
    /// Times are shown in local time, so a baseline is only comparable on the machine and time zone it came from.</summary>
    internal static class ShotRig
    {
        private static readonly StringBuilder _log = new StringBuilder();
        private static int _failures;
        private static string _dir;

        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The folder after "--shoot", or null when the switch is absent. A bare "--shoot" means .\shots.</summary>
        internal static string ParseDir(string[] args)
        {
            if (args == null) return null;
            for (int i = 1; i < args.Length; i++)
                if (string.Equals(args[i], "--shoot", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "shots");
            return null;
        }

        /// <summary>Returns the process exit code: 0 = every shot saved, 1 = a shot failed, 2 = isolation broken.</summary>
        internal static int Run(string dir, string isolatedRoot)
        {
            _dir = dir;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Fail("UI thread", e.Exception);
            Directory.CreateDirectory(dir);

            _log.Append(DiagReport.Build());
            _log.AppendLine("Isolated data root: " + isolatedRoot);
            _log.AppendLine();
            string live = null;
            try { live = Environment.GetEnvironmentVariable("TELEGARM_LIVE"); } catch { }
            if (live == "1") return RunLive(dir);
            if (live == "placement") return RunLivePlacement(dir);
            if (live == "keys") return RunLiveKeys(dir);
            if (live == "columns") return RunLiveColumns(dir);
            if (live == "media") return RunLiveMedia(dir);
            if (live == "camera") return RunLiveCamera(dir);

            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                ThemeHelper.InitMode(mode);   // InitMode raises no ThemeChanged, so no live form re-themes mid-run
                // (Redesign 2.7a: once MainForm became an AppForm the rig stopped creating MaterialSkin's manager; its Roboto
                //  registration had changed later text metrics, so B1 was refreshed then. 2.8 removed the package.)
                string t = mode == ThemeMode.Light ? "light" : "dark";

                // The real first view is QR, but ShowView(Qr) starts a QR login (network). Phone is UI-only.
                WithForm("login-" + t, () => new LoginForm(new TelegramService()), f =>
                {
                    var view = typeof(LoginForm).GetNestedType("View", BindingFlags.NonPublic);
                    if (view == null) throw new MissingMemberException("LoginForm", "View");
                    Invoke(f, "ShowView", Enum.Parse(view, "Phone"));
                    Capture(f, "login-phone-" + t);
                });

                long mainCtorMs = -1;
                WithForm("main-" + t, () =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var m = new MainForm(new TelegramService());
                    mainCtorMs = sw.ElapsedMilliseconds;   // step 2.9 (B2): the constructor, BuildUi included
                    return m;
                }, f =>
                {
                    Capture(f, "main-empty-" + t);
                    // Redesign 2.7b: the proxy pill in the title bar ("Connecting...", the on-accent variant).
                    ProxyStatus.NoteAttempt();
                    Invoke(f, "RefreshProxyPill");
                    Settle(f);
                    Capture(f, "main-proxy-" + t);
                    // Where the pill really is: DrawToBitmap renders a container's children in reverse order ("Controls inside
                    // containers are rendered in reverse order", MSDN), so the native-chrome FLOATING pill is painted under the
                    // list in a shot even when it is placed right. This line is the check for that case.
                    var pill = Field(f, "_proxyPill") as Control;
                    if (pill != null)
                        _log.AppendLine("  pill: in " + (pill.Parent != null ? pill.Parent.GetType().Name : "nothing") + ", shown="
                            + Field(f, "_proxyPillShown") + ", bounds " + pill.Bounds
                            + (pill.Parent != null ? " of " + pill.Parent.ClientSize : "") + ", front=" + (pill.Parent != null && pill.Parent.Controls.GetChildIndex(pill) == 0));
                    // Step 6.5: the WebSocket tunnel's pill — the shield when DC 2 went through the gateway, the wide
                    // "Connected directly" pill when it fell back (a touch screen has no tooltip to tell it apart).
                    AppSettings.Instance.WsTunnelEnabled = true;
                    ProxyStatus.MainDc = 2;
                    WsTunnelStats.Record(2, false, WsRouteKind.WebSocket, "kws2.web.telegram.org", null, 1100);
                    ProxyStatus.Reset();
                    ProxyStatus.NoteAuthorized();
                    Invoke(f, "RefreshProxyPill");
                    Settle(f);
                    Capture(f, "main-tunnel-ws-" + t);
                    WsTunnelStats.Record(2, false, WsRouteKind.Direct, "149.154.167.50:443", "no address", 3);
                    Invoke(f, "RefreshProxyPill");
                    Settle(f);
                    Capture(f, "main-tunnel-direct-" + t);
                    var tunnelPill = Field(f, "_proxyPill") as Control;
                    if (tunnelPill != null) _log.AppendLine("  tunnel pill: " + tunnelPill.Bounds + " caption via the tooltip state");
                    AppSettings.Instance.WsTunnelEnabled = false;
                    WsTunnelStats.Reset();
                    ProxyStatus.MainDc = 0;
                    ProxyStatus.Reset();
                    ProxyStatus.NoteAuthorized();   // direct: Off, the pill hides
                    Invoke(f, "RefreshProxyPill");
                    SeedChatList(f);
                    Capture(f, "main-chats-" + t);
                    OpenDemoChat(f);
                    Capture(f, "main-chat-" + t);
                    // The transfers indicator in the chat header, beside the ⋮ / magnifier / dock buttons (it shows only while
                    // transfers exist, so the rig forces it): its glyph must sit on the same centre line as theirs.
                    var dl = Field(f, "_dlIndicator") as Control;
                    if (dl != null) { dl.Visible = true; Settle(f); Capture(f, "main-transfers-" + t); dl.Visible = false; }
                    // Plan E3: a five-line draft — the colour-emoji composer grows with it (Plain stays one row).
                    var ib = Field(f, "_messageInput");
                    ib.GetType().GetProperty("Enabled").SetValue(ib, true);
                    ib.GetType().GetProperty("Text").SetValue(ib, "Draft line one \U0001F600\nline two\nline three \u2764\uFE0F\nline four\nline five");
                    Settle(f);
                    Capture(f, "main-draft-" + t);
                    RecordNumbers("main-" + t, mainCtorMs);
                });

                WithForm("settings-" + t, () => new SettingsForm(), f =>
                {
                    // Step 2.6: the rail's eleven pages. Offline (no service), Privacy / Security / Devices show their
                    // not-signed-in state — still worth a shot for the layout.
                    string[] names = { "general", "appearance", "notifications", "chats", "data", "connection",
                                       "privacy", "security", "devices", "advanced", "about" };
                    for (int i = 0; i < names.Length; i++)
                    {
                        Invoke(f, "ShowPage", i);
                        Settle(f);
                        Capture(f, "settings-" + names[i] + "-" + t);
                        var page = ((Panel[])Field(f, "_pages"))[i] as ScrollableControl;
                        if (page != null && page.DisplayRectangle.Height > page.ClientSize.Height)
                        {
                            page.AutoScrollPosition = new Point(0, page.DisplayRectangle.Height);
                            Settle(f);
                            Capture(f, "settings-" + names[i] + "-end-" + t);
                        }
                    }
                });

                WithForm("dialog-" + t, NewThemedDialog, f => Capture(f, "dialog-" + t));
                WithForm("about-" + t, () => new AboutForm(ThemeHelper.IsDark, ThemeHelper.GetWindowsAccentColor()),
                         f => Capture(f, "about-" + t));
                WithForm("proxy-" + t, () => new ProxyForm(), f =>
                {
                    Capture(f, "proxy-" + t);
                    // Step 6.5: the other two modes of the Connection strip (Proxy, WebSocket tunnel) — note, list and footer.
                    var strip = Field(f, "_mode");
                    foreach (var m in new[] { Tuple.Create(1, "proxy-mtproto-"), Tuple.Create(2, "proxy-tunnel-") })
                    {
                        strip.GetType().GetMethod("SetSelected").Invoke(strip, new object[] { m.Item1, true });
                        Settle(f);
                        Capture(f, m.Item2 + t);
                    }
                    // the dialog saves on close: leave it on Direct, or the next theme's shots start in tunnel mode
                    strip.GetType().GetMethod("SetSelected").Invoke(strip, new object[] { 0, true });
                });
                WithForm("gallery-" + t, () => new DesignGalleryForm(), f =>
                {
                    Capture(f, "gallery-" + t);   // step 1.3+
                    // Step 1.6: the page scrolls — one shot per screen of it, so every kit control is seen.
                    var g = (DesignGalleryForm)f;
                    for (int n = 1; n <= 8 && g.ScrollPage(n); n++) Capture(f, "gallery-" + t + "-" + n);
                });
                WithForm("confirm-" + t, () => ConfirmDialog.Create("Delete chat", "Delete this chat for you? This cannot be undone.", "Delete", "No", true),
                         f => Capture(f, "confirm-" + t));   // step 1.4+
                WithForm("input-" + t, () => InputDialog.Create("Rename", "New name", "Family group"), f => Capture(f, "input-" + t));
                WithForm("closechoice-" + t, () => new CloseChoiceDialog(), f => Capture(f, "closechoice-" + t));
                WithForm("colorpicker-" + t, () => new ColorPickerDialog("Accent colour", ThemeHelper.Palette.AccentSwatchBlue, null),
                         f => Capture(f, "colorpicker-" + t));   // step 5.2
                // ── Phase 2: the converted forms (one line each; constructible offline — no service call in the ctor) ──
                WithForm("p2-editcontact-" + t, () => new EditContactForm("Ada", "Lovelace", ThemeHelper.IsDark, ThemeHelper.Palette.Accent), f => Capture(f, "p2-editcontact-" + t));
                // Steps 2.2 / 2.6b (the parity reviewers named these constructors as offline-safe: nothing in a constructor
                // reaches the network or the camera — loads and the camera start on Shown, which the rig never raises).
                Shot("p2-createchat-" + t, () => new CreateChatForm(CreateChatForm.Kind.Channel, () => System.Threading.Tasks.Task.FromResult(new List<User>()), ThemeHelper.IsDark, ThemeHelper.Palette.Accent));
                Shot("p2-country-" + t, () => new CountryPickerForm(ThemeHelper.IsDark, ThemeHelper.Palette.Accent));
                Shot("p2-stories-" + t, () => new StoriesGridForm(null, null, Enumerable.Range(1, 9).Select(i => new StoryItem { id = i }).ToList(), ThemeHelper.IsDark, ThemeHelper.Palette.Accent, null));
                Shot("p2-forward-" + t, () => new ForwardPickerDialog(new List<ChatEntry> { new ChatEntry { PeerId = 1, Title = "Ada Lovelace" }, new ChatEntry { PeerId = 2, Title = "Family group" }, new ChatEntry { PeerId = 3, Title = "Charles Babbage" } }, ThemeHelper.IsDark, ThemeHelper.Palette.Accent));
                Shot("p2-people-" + t, () => new PeoplePickerForm(() => System.Threading.Tasks.Task.FromResult(new List<User>()), true, ThemeHelper.IsDark, ThemeHelper.Palette.Accent, "Add members"));
                Shot("p2-sendmedia-" + t, () => new SendMediaDialog(null, null, new List<string> { "holiday.jpg", "clip.mp4", "notes.pdf" }));
                Shot("p2-photoviewer-" + t, () => new ProfilePhotoViewer(new List<Image> { DemoImage(Color.SteelBlue), DemoImage(Color.SeaGreen), DemoImage(Color.Goldenrod) }));
                Shot("p2-proxylink-" + t, () => new ProxyLinkForm(DemoProxyUrl, false));
                Shot("p2-proxyqr-" + t, () => (Form)Activator.CreateInstance(typeof(ProxyShare).GetNestedType("QrDialog", BindingFlags.NonPublic), DemoProxyUrl));
                Shot("p2-rights-" + t, () => new TelegArm.UI.Admin.RightsChecklistForm("Admin rights", "Ada Lovelace", new List<TelegArm.UI.Admin.RightsChecklistForm.Item>
                    { new TelegArm.UI.Admin.RightsChecklistForm.Item("Change group info", true), new TelegArm.UI.Admin.RightsChecklistForm.Item("Delete messages", true),
                      new TelegArm.UI.Admin.RightsChecklistForm.Item("Ban users", false), new TelegArm.UI.Admin.RightsChecklistForm.Item("Invite users", true) }, ThemeHelper.IsDark, ThemeHelper.Palette.Accent));
                Shot("p2-managechat-" + t, () => new TelegArm.UI.Admin.ManageChatForm(null, new Channel { flags = Channel.Flags.megagroup }, null, "Sample group", "", ThemeHelper.IsDark, ThemeHelper.Palette.Accent));
                Shot("p2-editchatinfo-" + t, () => new TelegArm.UI.Admin.EditChatInfoForm(null, new Channel { title = "Sample group" }, null, "Sample group", "A sample description.", ThemeHelper.IsDark, ThemeHelper.Palette.Accent));
                Shot("p2-members-" + t, () => new TelegArm.UI.Admin.ChatMembersForm(null, new Channel { id = 1, flags = Channel.Flags.megagroup }, TelegArm.UI.Admin.ChatMembersForm.Mode.Members, ThemeHelper.IsDark, ThemeHelper.Palette.Accent));
                Shot("p2-invitelinks-" + t, () => new TelegArm.UI.Admin.InviteLinksForm(null, new InputPeerChannel(1, 0), ThemeHelper.IsDark, ThemeHelper.Palette.Accent));
                Shot("p2-channelextras-" + t, () => new TelegArm.UI.Admin.ChannelExtrasForm(null, new Channel { id = 1, username = "demo", flags = Channel.Flags.broadcast | Channel.Flags.signatures | Channel.Flags.creator }, ThemeHelper.IsDark, ThemeHelper.Palette.Accent));
                Shot("p2-roundrecorder-" + t, () => new RoundRecorderForm(null, null));
                // Step 2.5: the profile, modal (view of a user, filled from PeerInfo — LoadDetails runs on Load, which the rig
                // never raises) and the own-profile editor (Me is null offline, so its fields are empty).
                Shot("p2-profile-" + t, () => new ProfileForm(new TelegramService(), new ChatEntry
                {
                    Peer = new InputPeerUser(1001, 0), PeerId = 1001, Title = "Ada Lovelace",
                    PeerInfo = new User { id = 1001, first_name = "Ada", last_name = "Lovelace", username = "ada", phone = "15550100" },
                }, null));
                Shot("p2-profile-edit-" + t, () => new ProfileForm(new TelegramService()));
                Shot("trip-inputlab-" + t, () => new InputLabForm());   // device trip 2026-10: --input-lab
                // Step 3.1: the emoji picker as a popup (no service: the sticker/GIF tabs stay empty).
                Shot("p3-emoji-" + t, () => new EmojiPicker(null, ThemeHelper.IsDark, ThemeHelper.GetWindowsAccentColor()));
                // T9: the Reactions dialog's chrome (no service: the list says it couldn't load)
                WithForm("p7-reactors-" + t, () => new PeopleListForm("Reactions (12)", null), f => { Settle(f); Capture(f, "p7-reactors-" + t); });
                Shot("p7-addcontact-" + t, () => new AddContactForm());   // T15
                Shot("p7-inviteopts-" + t, () => new TelegArm.UI.Admin.InviteOptionsForm());   // T21
                Shot("p7-scheduled-" + t, () => new ScheduledForm(null, new TL.InputPeerSelf(), "Ada"));   // T11 (chrome)
                // 2026-10-16: the call window in four phases (an offline CallManager; no call is placed, no sound)
                UI.Ringer.Silent = true;
                var calls = new Core.Calls.CallManager(new TelegramService(), new WindowsFormsSynchronizationContext());
                var ada = new TL.User { id = 1001, first_name = "Ada", last_name = "Lovelace" };
                foreach (var ph in new[] { Core.Calls.CallPhase.Incoming, Core.Calls.CallPhase.Ringing, Core.Calls.CallPhase.Established, Core.Calls.CallPhase.Ended })
                {
                    var cs = new Core.Calls.CallSession { Peer = ada, PeerId = 1001, Outgoing = ph != Core.Calls.CallPhase.Incoming, Phase = ph };
                    if (ph == Core.Calls.CallPhase.Established) { cs.EstablishedUtc = DateTime.UtcNow.AddSeconds(-83); cs.SignalBars = 3; }
                    if (ph == Core.Calls.CallPhase.Ended) cs.EndText = "Call ended";
                    Shot("p8-call-" + ph.ToString().ToLowerInvariant() + "-" + t, () => new CallWindow(calls, cs, null, s => "Ada Lovelace"));
                }
                // T22: the privacy exceptions editor (two people in "Always allow")
                Shot("p7-privacyex-" + t, () => new PrivacyExceptionsForm("Last seen", new[] { new TL.User { id = 1001, first_name = "Ada", last_name = "Lovelace" },
                                                                                          new TL.User { id = 1002, first_name = "Charles" } }, null, null));
                // T13: a folder being edited (groups + muted left out, one chat always shown) and the plain chat chooser
                Shot("p7-folderedit-" + t, () => new FolderEditForm(new TL.DialogFilter
                {
                    id = 3, title = new TL.TextWithEntities { text = "Work", entities = new TL.MessageEntity[0] },
                    flags = TL.DialogFilter.Flags.groups | TL.DialogFilter.Flags.exclude_muted,
                    pinned_peers = new TL.InputPeer[0], include_peers = new TL.InputPeer[] { new TL.InputPeerUser(1, 0) }, exclude_peers = new TL.InputPeer[0],
                }, 4, DemoChats(), null, null));
                Shot("p7-folderpick-" + t, () => new ForwardPickerDialog(DemoChats(), "Always show", "Done", new long[] { DemoChats()[2].PeerId }, null, null));
                WithForm("p7-quiz-" + t, () => new CreatePollForm(ThemeHelper.IsDark, ThemeHelper.Palette.Accent), f =>   // T18
                {
                    var q = (CheckBox)Field(f, "_quiz"); q.Checked = true;
                    Settle(f);
                    Capture(f, "p7-quiz-" + t);
                });
                WithForm("p3-gifs-" + t, () => new EmojiPicker(null, ThemeHelper.IsDark, ThemeHelper.GetWindowsAccentColor()), f =>
                {
                    Invoke(f, "ShowTab", 2);   // T6: the GIF tab with its search box (no service: no tiles)
                    Settle(f);
                    Capture(f, "p3-gifs-" + t);
                });
                // Step 2.7d: the composer editor window (no service: its emoji picker is not opened here).
                Shot("editor-" + t, () => new ComposerEditorForm("Ada Lovelace",
                    "Draft line one \U0001F600\nline two with **bold** and __italic__\n\n"
                    + "A longer paragraph, to see how the editor wraps a line that is wider than its window. It should break between words.", null));
                // Plan E1: the colour-emoji composer — Latin with emoji, a Persian paragraph, mixed text, and a wrapped line.
                Shot("e1-composer-" + t, () =>
                {
                    var f = new Form { Size = new Size(640, 260), BackColor = ThemeHelper.Palette.Window };
                    var c = new TelegArm.UI.Controls.EmojiComposer { Dock = DockStyle.Fill, FontPoints = 11f };
                    c.Text = "Hello \U0001F600\U0001F44D\U0001F3FD colour emoji while typing \u2764\uFE0F\n"
                           + "\u0633\u0644\u0627\u0645 \u062F\u0646\u06CC\u0627 \U0001F339\n"
                           + "mixed: abc \u0633\u0644\u0627\u0645 123 and a long line that has to wrap onto the next one inside the box";
                    c.SelectionStart = 6; c.SelectionLength = 2;
                    f.Controls.Add(c);
                    return f;
                });
                // Step 2.3: a photo (bytes in memory, so no download) and a document — never a video (that would start VLC).
                WithForm("p2-viewer-" + t, () => new MediaViewerForm(new List<MediaItem>
                    {
                        new MediaItem { Type = "photo", FileName = "holiday.png", Bytes = DemoPng(Color.SteelBlue), Width = 640, Height = 400 },
                        new MediaItem { Type = "document", FileName = "notes.pdf", FileSize = 2516582 },
                    }, 0, null), f =>
                {
                    Capture(f, "p2-viewer-photo-" + t);
                    Invoke(f, "Navigate", 1);
                    Capture(f, "p2-viewer-doc-" + t);
                });
                ShootMenu("menu-" + t);
            }

            int code = _failures == 0 ? 0 : 1;
            string[] sessions = new string[0];
            try { sessions = Directory.GetFiles(isolatedRoot, "*session*", SearchOption.AllDirectories); } catch { }
            if (sessions.Length > 0)
            {
                code = 2;
                _log.AppendLine("ISOLATION BROKEN: a session file appeared under the isolated root: " + string.Join(", ", sessions));
            }
            else _log.AppendLine("Isolation held: no session file was created.");
            _log.AppendLine(_failures == 0 ? "All shots saved." : _failures + " failure(s).");
            File.WriteAllText(Path.Combine(dir, "shots.txt"), _log.ToString(), new UTF8Encoding(false));
            return code;
        }

        // ── form lifecycle ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>The LIVE rig (owner 2026-10-15: "your testing has a problem" — WM_PRINT shots never show the real window).
        /// TELEGARM_LIVE=1 with --shoot: the demo MainForm is SHOWN on the primary screen (same isolation: no account, an
        /// empty throwaway profile), topmost, and the real screen is captured in each state — normal, maximised, restored,
        /// then the same at a 125% interface scale, then back at 100% — with the window's geometry in shots.txt.</summary>
        private static int RunLive(string dir)
        {
            string theme = null;
            try { theme = Environment.GetEnvironmentVariable("TELEGARM_LIVE_THEME"); } catch { }
            ThemeHelper.InitMode(theme == "light" ? ThemeMode.Light : ThemeMode.Dark);
            var trace = new System.Diagnostics.TextWriterTraceListener(Path.Combine(dir, "trace.txt"));
            System.Diagnostics.Trace.Listeners.Add(trace);
            System.Diagnostics.Trace.AutoFlush = true;
            Logger.Enabled = true;   // the live rig only: [DPI] / [CHROME] lines go to trace.txt
            MainForm f = null;
            MainForm.DemoNoSession = true;   // shown, but never resumes a session (no account, no login window)
            try
            {
                f = new MainForm(new TelegramService());
                var wa = Screen.PrimaryScreen.WorkingArea;
                f.StartPosition = FormStartPosition.Manual;
                f.Bounds = new Rectangle(wa.Left + wa.Width / 10, wa.Top + wa.Height / 12, wa.Width * 7 / 10, wa.Height * 3 / 4);
                f.TopMost = true;
                f.Show();
                LivePump(1200);
                SeedChatList(f);
                OpenDemoChat(f);
                LivePump(800);
                LiveShot(f, "live-1-normal");
                f.WindowState = FormWindowState.Maximized; LivePump(1200);
                LiveShot(f, "live-2-maximised");
                f.WindowState = FormWindowState.Normal; LivePump(1200); LiveShot(f, "live-3-restored");
                AppForm.ApplyInterfaceScale(125); LivePump(1500); LiveShot(f, "live-4-scale125");
                f.WindowState = FormWindowState.Maximized; LivePump(1200); LiveShot(f, "live-5-maximised125");
                f.WindowState = FormWindowState.Normal; LivePump(1200); LiveShot(f, "live-6-restored125");
                AppForm.ApplyInterfaceScale(100); LivePump(1500); LiveShot(f, "live-7-back100");
                f.WindowState = FormWindowState.Maximized; LivePump(1200);
                AppForm.ApplyInterfaceScale(125); LivePump(1500); LiveShot(f, "live-8-scale125-while-maximised");
                AppForm.ApplyInterfaceScale(100); LivePump(1500); LiveShot(f, "live-9-back100-while-maximised");
            }
            catch (Exception ex) { Fail("live", ex); }
            finally
            {
                if (f != null) { try { f.Close(); } catch { } try { f.Dispose(); } catch { } }
            }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>Redesign R1, on the real screen: a saved placement comes back (normal and maximised), a moved window
        /// is saved in logical size, and a position on no screen falls back to centred. Isolated settings, no account.</summary>
        private static int RunLivePlacement(string dir)
        {
            ThemeHelper.InitMode(ThemeMode.Dark);
            var trace = new System.Diagnostics.TextWriterTraceListener(Path.Combine(dir, "trace.txt"));
            System.Diagnostics.Trace.Listeners.Add(trace);
            System.Diagnostics.Trace.AutoFlush = true;
            Logger.Enabled = true;
            MainForm.DemoNoSession = true;
            var s = AppSettings.Instance;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Func<string, MainForm> open = name =>
            {
                var m = new MainForm(new TelegramService());
                m.TopMost = true;
                m.Show();
                LivePump(1200);
                LiveShot(m, name);
                return m;
            };
            Action<MainForm> close = m => { try { m.Close(); } catch { } try { m.Dispose(); } catch { } LivePump(300); };
            Action<string> saved = what =>
            {
                var p = s.MainWindow;
                _log.AppendLine("  " + what + ": saved " + (p == null ? "null" : p.X + "," + p.Y + " " + p.Width + "x" + p.Height + " max=" + p.Maximized));
            };
            try
            {
                // 1. a saved placement comes back, normal
                s.MainWindow = new WindowPlacementSettings { X = wa.Left + 60, Y = wa.Top + 40, Width = 900, Height = 560 };
                var f = open("place-1-restored-normal");
                // 2. moved and resized by the user, then closed for real: saved in logical size
                f.Bounds = new Rectangle(wa.Left + 120, wa.Top + 90, f.Width + 80, f.Height + 40); LivePump(600);
                Invoke(f, "SaveWindowPlacement"); saved("after move");
                // 3. maximised: the normal bounds stay, the flag is set
                f.WindowState = FormWindowState.Maximized; LivePump(1200);
                Invoke(f, "SaveWindowPlacement"); saved("maximised");
                // 4. minimised from maximised: still remembered as maximised
                f.WindowState = FormWindowState.Minimized; LivePump(800);
                Invoke(f, "SaveWindowPlacement"); saved("minimised from maximised");
                close(f);
                // 5. the next start comes back maximised, and restoring returns to the saved normal bounds
                f = open("place-2-restored-maximised");
                f.WindowState = FormWindowState.Normal; LivePump(1200); LiveShot(f, "place-3-then-normal");
                close(f);
                // 6. a position on no screen (a monitor that is gone): centred at the saved size
                s.MainWindow = new WindowPlacementSettings { X = -30000, Y = -30000, Width = 900, Height = 560 };
                f = open("place-4-offscreen-centred");
                close(f);
                // 7. bigger than the screen: shrunk into the working area
                s.MainWindow = new WindowPlacementSettings { X = wa.Left, Y = wa.Top, Width = 6000, Height = 4000 };
                f = open("place-5-too-big");
                close(f);
            }
            catch (Exception ex) { Fail("live-placement", ex); }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>Redesign R5, on the real screen: Ctrl+E edits the last own message, Esc steps back one layer at a time
        /// (the edit, then the chat), Ctrl+1 picks All. The keys go through TryShortcut / EscapeOneLevel, the same calls
        /// ProcessCmdKey makes. No account: next-chat and mute need the network and are left out.</summary>
        /// <summary>Owner addition 2026-10-15 — the one-column layout, in both folder modes: two columns → narrow (the list)
        /// → a chat (back arrow + unread) → in-chat search (the results on the list side) → back → wide again → the minimum.</summary>
        private static int RunLiveColumns(string dir)
        {
            ThemeHelper.InitMode(ThemeMode.Light);
            var trace = new System.Diagnostics.TextWriterTraceListener(Path.Combine(dir, "trace.txt"));
            System.Diagnostics.Trace.Listeners.Add(trace);
            System.Diagnostics.Trace.AutoFlush = true;
            Logger.Enabled = true;
            MainForm.DemoNoSession = true;
            foreach (bool sidebar in new[] { false, true })
            {
                AppSettings.Instance.FolderSidebar = sidebar;   // in memory only (the rig's data root is isolated anyway)
                string m = sidebar ? "rail" : "tabs";
                MainForm f = null;
                Func<int, int> px = v => v * (f != null ? f.WindowDpiValue : 96) / 96;
                Action<string> state = what =>
                {
                    var split = (SplitContainer)Field(f, "_split");
                    var back = Field(f, "_backBtn") as Control;
                    var rail = Field(f, "_folderRail") as Control;
                    var barHost = Field(f, "_folderBarHost") as Control;
                    _log.AppendLine("  " + m + " " + what + ": w=" + f.Width + " one=" + Field(f, "_oneColumn") + " list=" + !split.Panel1Collapsed
                        + " chat=" + !split.Panel2Collapsed + " open=" + (Field(f, "_selectedChat") != null) + " back=" + (back != null && back.Visible)
                        + (back != null ? "/" + back.Width : "") + " rail=" + (rail != null ? rail.Visible.ToString() : "-")
                        + " strip=" + (barHost != null ? barHost.Visible + "/" + barHost.Height : "-") + " min=" + f.MinimumSize.Width);
                };
                try
                {
                    f = new MainForm(new TelegramService());
                    var wa = Screen.PrimaryScreen.WorkingArea;
                    f.StartPosition = FormStartPosition.Manual;
                    f.Bounds = new Rectangle(wa.Left + 60, wa.Top + 40, 1000 * 96 / 96, 700);
                    f.TopMost = true;
                    f.Show();
                    LivePump(1200);
                    f.Bounds = new Rectangle(wa.Left + 60, wa.Top + 40, px(1000), px(700));
                    SeedChatList(f);
                    SetField(f, "_folders", new DialogFilterBase[]
                    {
                        null,
                        new DialogFilter { id = 2, title = new TextWithEntities { text = "Work", entities = new MessageEntity[0] }, flags = DialogFilter.Flags.groups,
                                           pinned_peers = new InputPeer[0], include_peers = new InputPeer[0], exclude_peers = new InputPeer[0] },
                        new DialogFilter { id = 3, title = new TextWithEntities { text = "Family", entities = new MessageEntity[0] }, flags = DialogFilter.Flags.contacts,
                                           pinned_peers = new InputPeer[0], include_peers = new InputPeer[0], exclude_peers = new InputPeer[0] },
                    });
                    Invoke(f, "RebuildFolders");
                    LivePump(600);
                    LiveShot(f, "col-" + m + "-1-two"); state("1 two columns");
                    f.Width = px(600); LivePump(800);
                    LiveShot(f, "col-" + m + "-2-one-list"); state("2 narrowed");
                    OpenDemoChat(f);
                    Invoke(f, "ShowColumn", false);   // what OpenChat does right after it sets _selectedChat
                    Invoke(f, "RefreshFolderBadges");
                    LivePump(800);
                    LiveShot(f, "col-" + m + "-3-one-chat"); state("3 chat opened");
                    Invoke(f, "EnterInChatSearch"); LivePump(600);
                    LiveShot(f, "col-" + m + "-4-inchat-search"); state("4 in-chat search");
                    Invoke(f, "ExitInChatSearch"); LivePump(400); state("4b search exited");
                    Invoke(f, "OnBackClick"); LivePump(600);
                    LiveShot(f, "col-" + m + "-5-back"); state("5 back");
                    OpenDemoChat(f); Invoke(f, "ShowColumn", false); LivePump(400);
                    f.Width = px(1000); LivePump(800);
                    LiveShot(f, "col-" + m + "-6-two-again"); state("6 widened with a chat open");
                    Invoke(f, "OnBackClick"); LivePump(300);   // the list, to see the folders near the minimum
                    f.Width = px(460); LivePump(800);
                    LiveShot(f, "col-" + m + "-7-near-minimum"); state("7 near the minimum (460)");
                    f.Width = px(300); LivePump(800);
                    LiveShot(f, "col-" + m + "-8-minimum"); state("8 dragged to 300");
                    f.Width = px(600); LivePump(800); LiveShot(f, "col-" + m + "-9-600"); state("9 back to 600");
                }
                catch (Exception ex) { Fail("live-columns-" + m, ex); }
                finally { if (f != null) { try { f.Close(); } catch { } try { f.Dispose(); } catch { } } }
            }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>2026-10-16 — the FFmpeg player in a real window (no account, no user media): ffmpeg-rt's synthetic H.264
        /// clip in VideoPlayerControl (play, seek, pause, resize, loop), a voice note made here with Concentus through
        /// AudioPlayer (1x then 2x), WebmAnimator, VideoProbe and GrabFirstFrame. Every check logs PASS/FAIL.</summary>
        private static int RunLiveMedia(string dir)
        {
            ThemeHelper.InitMode(ThemeMode.Dark);
            var trace = new System.Diagnostics.TextWriterTraceListener(Path.Combine(dir, "trace.txt"));
            System.Diagnostics.Trace.Listeners.Add(trace);
            System.Diagnostics.Trace.AutoFlush = true;
            Logger.Enabled = true;
            Action<bool, string> expect = (ok, what) => { _log.AppendLine("  " + (ok ? "PASS  " : "FAIL  ") + what); if (!ok) _failures++; };
            string clip = Environment.GetEnvironmentVariable("TELEGARM_TEST_CLIP") ?? @"D:\repo\ffmpeg-rt\dist\device-trip\h264_360p.mp4";
            Form f = null;
            try
            {
                expect(Core.Media.Tgav.Available, "media engine: " + Core.Media.Tgav.Status);
                expect(File.Exists(clip), "test clip " + clip);
                f = new Form { Text = "media rig", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(80, 80, 900, 560), TopMost = true, BackColor = Color.Black };
                var v = new UI.Controls.VideoPlayerControl { Dock = DockStyle.Fill };
                f.Controls.Add(v);
                f.Show();
                LivePump(400);

                v.PlayFile(clip);
                LivePump(2500);
                long t1 = v.Time;
                expect(v.State == Core.Media.MediaState.Playing, "video playing (state " + v.State + ")");
                expect(v.Length > 20000, "length " + v.Length + " ms");
                // the clock is what the audio device has PLAYED: its output latency (large over Remote Desktop) shows up as
                // a late start, not as a wrong rate — so the rate is what is checked; the lag is logged
                _log.AppendLine("  info  start lag " + (2500 - t1) + " ms (audio output latency + open)");
                LivePump(2000);
                long t1b = v.Time;
                expect(Math.Abs((t1b - t1) - 2000) < 300, "clock rate: " + (t1b - t1) + " ms per 2000 ms");
                FormShot(f, "media-1-playing");

                v.Position = 0.5f;
                LivePump(1000);
                long t2 = v.Time;
                expect(Math.Abs(t2 - (v.Length / 2 + 1000)) < 600, "after a seek to the middle + 1 s: " + t2 + " ms (~" + (v.Length / 2 + 1000) + ")");

                v.TogglePlay(); LivePump(400);
                long p1 = v.Time; LivePump(800); long p2 = v.Time;
                expect(v.State == Core.Media.MediaState.Paused && p1 == p2, "paused: the time holds (" + p1 + " = " + p2 + ")");
                v.TogglePlay(); LivePump(800);
                expect(v.Time > p2 + 400, "resumed: " + v.Time + " ms");

                f.Width = 520; LivePump(900);
                FormShot(f, "media-2-resized");
                var pb = v.Playback;
                int serial = pb != null ? pb.Frames.Serial : -1;
                LivePump(500);
                expect(pb != null && pb.Frames.FrontWidth <= 520 && pb.Frames.Serial > serial, "frames follow the new size (" + (pb != null ? pb.Frames.FrontWidth + "x" + pb.Frames.FrontHeight : "-") + ")");

                v.PlayFile(clip, loop: true);
                LivePump(800);
                v.Position = 0.99f;
                LivePump(1500);
                expect(v.State == Core.Media.MediaState.Playing && v.Time < v.Length / 2, "loop: back at the start (" + v.Time + " ms, " + v.State + ")");
                v.StopPlayback();

                // a voice note through AudioPlayer: 1x for a second, then 2x (pitch kept by atempo)
                string ogg = Path.Combine(Path.GetTempPath(), "tgav_rig_" + Guid.NewGuid().ToString("N") + ".ogg");
                using (var fs = File.Create(ogg))
                {
                    var enc = Concentus.Structs.OpusEncoder.Create(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
                    var w = new Concentus.Oggfile.OpusOggWriteStream(enc, fs);
                    var pcm = new short[48000 * 3];
                    for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(9000 * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
                    w.WriteSamples(pcm, 0, pcm.Length);
                    w.Finish();
                }
                expect(AudioPlayer.Toggle(4242, ogg, "rig voice note"), "AudioPlayer took the voice note");
                LivePump(1100);
                long a1 = AudioPlayer.TimeMs;
                expect(AudioPlayer.IsPlaying && a1 > 600 && a1 < 1400, "voice note at 1x after 1.1 s: " + a1 + " ms (" + AudioPlayer.State + ")");
                AudioPlayer.SetRate(2f);
                LivePump(1700);
                expect(AudioPlayer.State == Core.Media.MediaState.Ended, "at 2x the rest (~2 s of audio) ended within 1.7 s (" + AudioPlayer.State + ", " + AudioPlayer.TimeMs + " ms)");
                AudioPlayer.Stop();
                try { File.Delete(ogg); } catch { }

                int frames = 0;
                using (var anim = new UI.Controls.WebmAnimator(clip, 160, 90, 30, f, img => { frames++; img.Dispose(); }))
                    LivePump(1500);
                expect(frames >= 25, "WebmAnimator delivered " + frames + " frames in 1.5 s (fps cap 30)");

                var info = VideoProbe.ProbeAsync(clip, System.Threading.CancellationToken.None).Result;
                expect(info.Width == 640 && info.Height == 360 && info.DurationSec >= 29, "VideoProbe " + info.Width + "x" + info.Height + " " + info.DurationSec + " s");
                expect(info.ThumbPath != null && File.Exists(info.ThumbPath), "VideoProbe poster " + info.ThumbPath);
                if (info.ThumbPath != null) try { File.Copy(info.ThumbPath, Path.Combine(dir, "media-3-poster.jpg"), true); File.Delete(info.ThumbPath); } catch { }
                using (var bmp = UI.Controls.WebmAnimator.GrabFirstFrame(clip, 320, 180))
                {
                    expect(bmp != null && bmp.Width == 320, "GrabFirstFrame " + (bmp != null ? bmp.Width + "x" + bmp.Height : "null"));
                    if (bmp != null) bmp.Save(Path.Combine(dir, "media-4-firstframe.png"), ImageFormat.Png);
                }
            }
            catch (Exception ex) { Fail("live-media", ex); }
            finally { if (f != null) { try { f.Close(); } catch { } try { f.Dispose(); } catch { } } }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>2026-10-16 — the round-video camera, both backends, through CameraCapture (the recorder's own path). It
        /// MEASURES and saves no picture (the camera may face the owner): preview frame rate, then a 3 s recording probed with
        /// FFmpeg (size, length, audio) and deleted.</summary>
        private static int RunLiveCamera(string dir)
        {
            ThemeHelper.InitMode(ThemeMode.Dark);
            var trace = new System.Diagnostics.TextWriterTraceListener(Path.Combine(dir, "trace.txt"));
            System.Diagnostics.Trace.Listeners.Add(trace);
            System.Diagnostics.Trace.AutoFlush = true;
            Logger.Enabled = true;
            Action<bool, string> expect = (ok, what) => { _log.AppendLine("  " + (ok ? "PASS  " : "FAIL  ") + what); if (!ok) _failures++; };
            foreach (string backend in new[] { "framereader", "winrt" })
            {
                Form f = null;
                Core.Camera.ICameraRecorder rec = null;
                string mp4 = Path.Combine(Path.GetTempPath(), "tgcam_rig_" + Guid.NewGuid().ToString("N") + ".mp4");
                try
                {
                    _log.AppendLine("  -- " + backend);
                    if (backend == "framereader") expect(Core.Camera.CameraCapture.FrameReaderSupported, "frame reader supported here");
                    Core.Camera.CameraCapture.Forced = backend;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    rec = RunSync(Core.Camera.CameraCapture.CreateAsync());
                    expect(rec != null, backend + " opened in " + sw.ElapsedMilliseconds + " ms (" + (rec != null ? rec.GetType().Name : "null") + ")");
                    if (rec == null) continue;
                    f = new Form { Text = "camera rig", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(80, 80, 360, 400), TopMost = true };
                    var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
                    f.Controls.Add(host);
                    f.Show();
                    expect(rec.TryAttachPreview(host), "preview attached");
                    var fr = rec as Core.Camera.FrameReaderCameraRecorder;
                    int d0 = fr != null ? fr.FramesDelivered : 0, p0 = fr != null ? fr.PreviewFrames : 0;
                    LivePump(3000);
                    if (fr != null)
                    {
                        double fps = (fr.FramesDelivered - d0) / 3.0, pfps = (fr.PreviewFrames - p0) / 3.0;
                        _log.AppendLine("  info  stream " + fr.StreamDescription);
                        expect(fps >= 12, "reader " + fps.ToString("0.0") + " fps over 3 s");
                        expect(pfps >= 12, "preview " + pfps.ToString("0.0") + " fps over 3 s (the photo poll managed ~1.6 on the Surface)");
                    }
                    RunSync(rec.StartAsync(mp4));
                    int r0 = fr != null ? fr.PreviewFrames : 0;
                    LivePump(3000);
                    if (fr != null) _log.AppendLine("  info  preview during recording: " + ((fr.PreviewFrames - r0) / 3.0).ToString("0.0") + " fps");
                    string path = RunSync(rec.StopAsync());
                    expect(File.Exists(path) && new FileInfo(path).Length > 1000, "recording written (" + (File.Exists(path) ? new FileInfo(path).Length + " bytes" : "missing") + ")");
                    string err;
                    using (var m = Core.Media.MediaFile.Open(path, 0, out err))
                    {
                        expect(m != null, "recording opens in FFmpeg (" + err + ")");
                        if (m != null)
                        {
                            expect(m.Info.Width == 384 && m.Info.Height == 384, "384x384 (" + m.Info.Width + "x" + m.Info.Height + ")");
                            expect(m.Info.DurationMs > 2000 && m.Info.DurationMs < 4500, "about 3 s (" + m.Info.DurationMs + " ms)");
                            _log.AppendLine("  info  audio track: " + (m.HasAudio ? m.Info.SampleRate + " Hz x" + m.Info.Channels : "none"));
                        }
                    }
                }
                catch (Exception ex) { expect(false, backend + ": " + ex.GetType().Name + " " + ex.Message); }
                finally
                {
                    try { if (rec != null) rec.Dispose(); } catch { }
                    if (f != null) { try { f.Close(); } catch { } try { f.Dispose(); } catch { } }
                    try { if (File.Exists(mp4)) File.Delete(mp4); } catch { }
                    Core.Camera.CameraCapture.Forced = null;
                    LivePump(500);
                }
            }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>Waits for a task while pumping the UI (the camera's preview paints on the UI thread meanwhile).</summary>
        private static T RunSync<T>(System.Threading.Tasks.Task<T> t)
        {
            while (!t.IsCompleted) LivePump(20);
            return t.GetAwaiter().GetResult();
        }

        private static void RunSync(System.Threading.Tasks.Task t)
        {
            while (!t.IsCompleted) LivePump(20);
            t.GetAwaiter().GetResult();
        }

        private static void FormShot(Form f, string name)
        {
            try { f.Activate(); } catch { }
            LivePump(250);
            var r = f.Bounds;
            using (var bmp = new Bitmap(r.Width, r.Height))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                bmp.Save(Path.Combine(_dir, name + ".png"), ImageFormat.Png);
            }
        }

        private static int RunLiveKeys(string dir)
        {
            ThemeHelper.InitMode(ThemeMode.Light);
            var trace = new System.Diagnostics.TextWriterTraceListener(Path.Combine(dir, "trace.txt"));
            System.Diagnostics.Trace.Listeners.Add(trace);
            System.Diagnostics.Trace.AutoFlush = true;
            Logger.Enabled = true;
            MainForm.DemoNoSession = true;
            MainForm f = null;
            Action<string> state = what =>
            {
                var box = Field(f, "_messageInput");
                string text = box != null ? (box.GetType().GetProperty("Text").GetValue(box, null) as string ?? "") : "?";
                _log.AppendLine("  " + what + ": chat=" + (Field(f, "_selectedChat") != null ? "open" : "none")
                    + " edit=" + (Field(f, "_editTarget") != null ? "yes" : "no") + " composer=[" + text.Replace('\n', ' ') + "]");
            };
            try
            {
                f = new MainForm(new TelegramService());
                var wa = Screen.PrimaryScreen.WorkingArea;
                f.StartPosition = FormStartPosition.Manual;
                f.Bounds = new Rectangle(wa.Left + wa.Width / 10, wa.Top + wa.Height / 12, wa.Width * 7 / 10, wa.Height * 3 / 4);
                f.TopMost = true;
                f.Show();
                LivePump(1200);
                SeedChatList(f);
                OpenDemoChat(f);
                LivePump(600);
                state("opened");
                var callBtn = Field(f, "_callBtn") as Control; var lensBtn = Field(f, "_chatSearchBtn") as Control;
                bool phoneOk = callBtn != null && callBtn.Visible && callBtn.Left < lensBtn.Left;
                _log.AppendLine("  " + (phoneOk ? "PASS  " : "FAIL  ") + "header: the phone sits left of the magnifier");
                if (!phoneOk) _failures++;
                // T20: a venue message — its name, address and an "Open in map" link
                var venueMsg = new TL.Message
                {
                    id = 9001, date = DateTime.UtcNow, peer_id = new TL.PeerUser { user_id = 900001 }, message = "",
                    media = new TL.MessageMediaVenue { title = "Café Babbage", address = "12 Engine Street", geo = new TL.GeoPoint { lat = 51.5007, lon = -0.1246 } },
                };
                Invoke(f, "AddMessageBubble", Field(f, "_selectedChat"), venueMsg, null, null);
                Invoke(f, "ScrollMessagesToBottom");
                LivePump(500);
                LiveShot(f, "keys-0b-location");
                // T2: the "@" and heart buttons, seeded with the demo chat's own message ids (the fetch needs an account)
                var msgs = ((IEnumerable)Field(f, "_currentChatMessages")).Cast<TL.Message>().Select(m => m.ID).OrderBy(i => i).ToList();
                SetField(f, "_jumpListChat", Field(f, "_selectedChat"));
                ((List<int>)Field(f, "_mentionIds")).AddRange(msgs.Take(2));
                ((List<int>)Field(f, "_reactionIds")).Add(msgs[0]);
                Invoke(f, "UpdateMentionButtons");
                LivePump(400);
                LiveShot(f, "keys-0-mention-buttons");
                _log.AppendLine("  mention button: visible=" + ((Control)Field(f, "_mentionBtn")).Visible + " at " + ((Control)Field(f, "_mentionBtn")).Bounds
                                + ", reaction: " + ((Control)Field(f, "_reactionBtn")).Bounds + ", jump: visible=" + ((Control)Field(f, "_jumpBtn")).Visible
                                + " at " + ((Control)Field(f, "_jumpBtn")).Bounds);
                Invoke(f, "JumpToNextUnread", true); LivePump(600);
                _log.AppendLine("  after a mention jump: " + ((List<int>)Field(f, "_mentionIds")).Count + " mentions left");
                Invoke(f, "JumpToNextUnread", true); Invoke(f, "JumpToNextUnread", false); LivePump(400);
                _log.AppendLine("  after jumping through all: mention visible=" + ((Control)Field(f, "_mentionBtn")).Visible
                                + " reaction visible=" + ((Control)Field(f, "_reactionBtn")).Visible);
                _log.AppendLine("  Ctrl+E → " + Invoke(f, "TryShortcut", Keys.Control | Keys.E)); LivePump(500);
                state("after Ctrl+E"); LiveShot(f, "keys-1-edit");
                _log.AppendLine("  Esc → " + Invoke(f, "EscapeOneLevel")); LivePump(400); state("after Esc 1");
                _log.AppendLine("  Esc → " + Invoke(f, "EscapeOneLevel")); LivePump(600); state("after Esc 2");
                LiveShot(f, "keys-2-closed");
                _log.AppendLine("  Esc → " + Invoke(f, "EscapeOneLevel") + " (nothing left)");
                _log.AppendLine("  Ctrl+1 → " + Invoke(f, "TryShortcut", Keys.Control | Keys.D1) + ", Ctrl+5 → " + Invoke(f, "TryShortcut", Keys.Control | Keys.D5));
                _log.AppendLine("  Ctrl+Shift+M with no chat → " + Invoke(f, "TryShortcut", Keys.Control | Keys.Shift | Keys.M));
                // T1: the Mute submenu as the chat menu builds it (themed strip, four durations), on the real screen
                var menu = new ThemedContextMenuStrip();
                var mute = new ToolStripMenuItem("\U0001F515   Mute");
                MuteMenu.AddChoices(mute.DropDownItems, f, d => { });
                menu.Items.Add(new ToolStripMenuItem("\U0001F4CC   Pin"));
                menu.Items.Add(mute);
                menu.ShowAt(f, new Point(f.ClientSize.Width / 3, f.ClientSize.Height / 4));
                LivePump(300);
                mute.ShowDropDown();
                LivePump(500);
                var area = Rectangle.Union(menu.Bounds, mute.DropDown.Bounds);
                area.Inflate(16, 16);
                using (var bmp = new Bitmap(area.Width, area.Height))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                    bmp.Save(Path.Combine(_dir, "keys-3-mute-menu.png"), ImageFormat.Png);
                }
                _log.AppendLine("  mute submenu: " + mute.DropDownItems.Count + " items");
                menu.Close();
                menu.Dispose();
                // T23: a few service actions in words (ServiceText is the one mapping every service line goes through)
                Func<TL.MessageAction, TL.Peer, bool, string> say = (act, peer, mine) => (string)Invoke(f, "ServiceText",
                    new TL.MessageService { action = act, peer_id = peer, flags = mine ? TL.MessageService.Flags.out_ : 0 });
                var dm = new TL.PeerUser { user_id = 900001 };
                var grp = new TL.PeerChat { chat_id = 77 };
                _log.AppendLine("  T23 outgoing call: " + say(new TL.MessageActionPhoneCall { duration = 330, flags = TL.MessageActionPhoneCall.Flags.has_duration }, dm, true));
                _log.AppendLine("  T23 missed video call: " + say(new TL.MessageActionPhoneCall { reason = new TL.PhoneCallDiscardReasonMissed(), flags = TL.MessageActionPhoneCall.Flags.video | TL.MessageActionPhoneCall.Flags.has_reason }, dm, false));
                _log.AppendLine("  T23 video chat ended: " + say(new TL.MessageActionGroupCall { duration = 4000 }, grp, false));
                _log.AppendLine("  T23 topic closed: " + say(new TL.MessageActionTopicEdit { closed = true, flags = TL.MessageActionTopicEdit.Flags.has_closed }, grp, false));
                _log.AppendLine("  T23 payment: " + say(new TL.MessageActionPaymentSent { total_amount = 1250, currency = "USD" }, dm, true));
                _log.AppendLine("  T23 migrate: " + say(new TL.MessageActionChatMigrateTo { channel_id = 5 }, grp, false));

                // T12: the reaction grid, with the service's reaction list pre-seeded (fetching it needs an account)
                var svc = Field(f, "_activeService");
                var seeded = new[] { "\U0001F44D", "\U0001F44E", "\u2764", "\U0001F525", "\U0001F970", "\U0001F44F", "\U0001F601", "\U0001F914",
                                     "\U0001F92F", "\U0001F631", "\U0001F92C", "\U0001F622", "\U0001F389", "\U0001F929", "\U0001F92E", "\U0001F4A9",
                                     "\U0001F64F", "\U0001F44C", "\U0001F54A", "\U0001F921", "\U0001F971", "\U0001F974", "\U0001F60D", "\U0001F433" };
                svc.GetType().GetField("_availableReactions", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(svc, seeded);
                var anchor = f.PointToScreen(new Point(f.ClientSize.Width / 3, f.ClientSize.Height / 4));
                Cursor.Position = anchor;
                var firstMsg = ((IEnumerable)Field(f, "_currentChatMessages")).Cast<TL.Message>().FirstOrDefault();
                Invoke(f, "ShowReactionGrid", firstMsg);
                LivePump(800);
                var grid = new Rectangle(anchor.X - 8, anchor.Y - 8, 420, 180);
                using (var bmp = new Bitmap(grid.Width, grid.Height))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(grid.Location, Point.Empty, grid.Size);
                    bmp.Save(Path.Combine(_dir, "keys-4-reaction-grid.png"), ImageFormat.Png);
                }
                // (the grid is left open: the form then closes with it open, the case the Closed guard covers)
                LivePump(300);
            }
            catch (Exception ex) { Fail("live-keys", ex); }
            finally { if (f != null) { try { f.Close(); } catch { } try { f.Dispose(); } catch { } } }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        private static object Member(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var pi = t.GetProperty(name, Inst | BindingFlags.DeclaredOnly);
                if (pi != null) return pi.GetValue(o, null);
                var fi = t.GetField(name, Inst | BindingFlags.DeclaredOnly);
                if (fi != null) return fi.GetValue(o);
            }
            return null;
        }

        private static void LivePump(int ms)
        {
            int end = Environment.TickCount + ms;
            while (Environment.TickCount - end < 0) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
        }

        /// <summary>The real screen around the window (the whole work area when maximised), and its geometry.</summary>
        private static void LiveShot(AppForm f, string name)
        {
            try { f.Activate(); } catch { }
            LivePump(300);
            var scr = Screen.FromHandle(f.Handle);
            var area = f.WindowState == FormWindowState.Maximized ? scr.Bounds : Rectangle.Inflate(f.Bounds, 24, 24);
            area.Intersect(scr.Bounds);
            using (var bmp = new Bitmap(area.Width, area.Height))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                bmp.Save(Path.Combine(_dir, name + ".png"), ImageFormat.Png);
            }
            var content = Member(f, "Content") as Control;
            var bar = Member(f, "Bar") as Control;
            _log.AppendLine("  " + name + ": state " + f.WindowState + " bounds " + f.Bounds + " client " + f.ClientSize
                            + " dpi " + f.WindowDpiValue + " padding " + f.Padding + " bar " + (bar != null ? bar.Bounds.ToString() : "-")
                            + " content " + (content != null ? content.Bounds.ToString() : "-") + " work " + scr.WorkingArea);
            var split = Field(f, "_split") as Control;
            if (split != null) _log.AppendLine("      split " + split.Bounds + " in " + (split.Parent != null ? split.Parent.ClientSize.ToString() : "-"));
            foreach (var n in new[] { "_rightLayout", "_headerBar", "_messagePanel", "_chatListPanel", "_replyKbHost" })
            {
                var c = Member(f, n) as Control;
                if (c != null) _log.AppendLine("      " + n + " " + c.Bounds + " in parent " + (c.Parent != null ? c.Parent.GetType().Name + " " + c.Parent.ClientSize : "-"));
            }
            var mp = Member(f, "_messagePanel") as Control;
            if (mp != null && mp.Controls.Count > 0) { var b = mp.Controls[mp.Controls.Count - 1]; _log.AppendLine("      last row " + b.GetType().Name + " " + b.Bounds); }
            if (bar != null)
            {
                // Win32's view: where the bar's HWND really is, and where the form's client origin is, on screen
                var origin = f.PointToScreen(Point.Empty);
                var barScreen = bar.RectangleToScreen(bar.ClientRectangle);
                _log.AppendLine("      win32: client origin " + origin + " bar on screen " + barScreen + " displayRect " + f.DisplayRectangle);
                f.PerformLayout();
                _log.AppendLine("      after PerformLayout: bar " + bar.Bounds + " content " + (content != null ? content.Bounds.ToString() : "-"));
            }
        }

        private static void WithForm(string name, Func<Form> make, Action<Form> shoot)
        {
            Form f = null;
            try
            {
                f = make();
                // On the PRIMARY monitor, always: a hidden form otherwise lands wherever Windows' default placement puts
                // it (the monitor under the cursor), and anything that depends on its monitor — AppForm's compact title
                // bar (work area ≤ 800 logical px), per-monitor DPI — made the shots depend on where the mouse was
                // (step 2.1: the gallery's bar flipped between compact and full between two runs).
                f.StartPosition = FormStartPosition.Manual;
                f.Location = Screen.PrimaryScreen.WorkingArea.Location;
                Realize(f);
                shoot(f);
            }
            catch (Exception ex) { Fail(name, ex); }
            finally
            {
                // Close (not just Dispose) so a form's own FormClosed teardown runs — MainForm unhooks its static
                // events there. Close on a never-shown form with a handle still raises FormClosing/FormClosed.
                if (f != null)
                {
                    try { f.Close(); } catch (Exception ex) { Fail(name + " (close)", ex); }
                    try { f.Dispose(); } catch { }
                }
                Settle(null);
            }
        }

        private const string DemoProxyUrl = "tg://proxy?server=proxy.example.com&port=443&secret=ee0123456789abcdef0123456789abcdef7777772e6578616d706c652e636f6d";

        /// <summary>Step 2.9 (baseline B2): what the dev box can measure with no account — MainForm's constructor (BuildUi
        /// included; the first one of a run is cold, the second warm), and the process's working set and GDI / USER handles
        /// with the main window up. They catch regressions between baselines; never rank a cold-start item from them (the
        /// RT device is 10-15x slower and differently bound — NOTES).</summary>
        private static void RecordNumbers(string name, long ctorMs)
        {
            uint gdi, usr;
            PerfLog.GuiHandles(out gdi, out usr);
            long ws = 0;
            try { using (var pr = System.Diagnostics.Process.GetCurrentProcess()) ws = pr.WorkingSet64; } catch { }
            _log.AppendLine("  NUMBERS " + name + ": ctor " + ctorMs + " ms, working set " + (ws / (1024 * 1024)) + " MB, gdi " + gdi + ", user " + usr);
        }

        /// <summary>A form shot in one line: build it, capture it under <paramref name="name"/>.</summary>
        private static void Shot(string name, Func<Form> make) { WithForm(name, make, f => Capture(f, name)); }

        /// <summary>A flat 400×400 picture (no file, no network) for the photo viewers.</summary>
        private static Image DemoImage(Color c)
        {
            var b = new Bitmap(400, 400);
            using (var g = Graphics.FromImage(b)) g.Clear(c);
            return b;
        }

        private static byte[] DemoPng(Color c)
        {
            using (var img = DemoImage(c))
            using (var ms = new MemoryStream())
            {
                img.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        private static readonly MethodInfo CreateControlIgnoringVisible =
            typeof(Control).GetMethod("CreateControl", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);

        /// <summary>Creates every handle with the window still hidden. Control.CreateControl(true) is what
        /// Show() would run, minus making it visible — which is exactly what keeps Form.OnLoad from firing.</summary>
        private static void Realize(Form f)
        {
            if (CreateControlIgnoringVisible == null) throw new MissingMethodException("Control", "CreateControl(bool)");
            CreateControlIgnoringVisible.Invoke(f, new object[] { true });
            Settle(f);
        }

        private static void Settle(Control c)
        {
            if (c != null) c.PerformLayout();
            for (int i = 0; i < 3; i++) Application.DoEvents();   // deferred BeginInvoke layout passes
        }

        /// <summary>A control added after Realize (chat rows, bubbles) gets no handle of its own: Controls.Add only
        /// creates one when the parent is VISIBLE, and nothing is here. No handle means WM_PRINT skips it, so the
        /// shot comes out empty. Create every missing handle, top-down, right before drawing.</summary>
        private static void RealizeMissing(Control c)
        {
            foreach (Control child in c.Controls)
            {
                if (!child.IsHandleCreated) CreateControlIgnoringVisible.Invoke(child, new object[] { true });
                else RealizeMissing(child);
            }
        }

        private static void Capture(Control c, string name)
        {
            RealizeMissing(c);
            Settle(c);
            int w = Math.Max(1, c.Width), h = Math.Max(1, c.Height);
            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))   // 24 bpp: WM_PRINT leaves alpha undefined
            {
                c.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
                // A form with native resize edges (Windows 10/11) has Windows' frame OUTSIDE its client; on screen that frame
                // is invisible, but WM_PRINT paints it. Keep only what the user sees: the client area.
                var form = c as Form;
                if (form != null && form.IsHandleCreated && form.ClientSize.Width > 0 && form.ClientSize.Height > 0 && form.ClientSize != form.Size)
                {
                    var client = form.RectangleToScreen(form.ClientRectangle);
                    var crop = new Rectangle(client.X - form.Bounds.X, client.Y - form.Bounds.Y, client.Width, client.Height);
                    crop.Intersect(new Rectangle(0, 0, w, h));
                    using (var cut = bmp.Clone(crop, bmp.PixelFormat)) cut.Save(Path.Combine(_dir, name + ".png"), ImageFormat.Png);
                    w = crop.Width; h = crop.Height;
                }
                else bmp.Save(Path.Combine(_dir, name + ".png"), ImageFormat.Png);
            }
            _log.AppendLine("  " + name + ".png  " + w + "x" + h);
        }

        private static void Fail(string what, Exception ex)
        {
            _failures++;
            _log.AppendLine("  FAILED " + what + ": " + ex);
        }

        // ── the demo data: fixed, past, and free of anything real ────────────────────────────────────────────

        private static readonly DateTime Day = new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc);
        private const long AdaId = 900001, TestersId = 900002, NotesId = 900003, SaraId = 900004, WeekendId = 900005, CharlesId = 900006;

        private static ChatEntry Entry(long id, InputPeer peer, string title, string preview, int minutesBefore, int unread)
        {
            return new ChatEntry
            {
                Peer = peer, PeerId = id, Title = title, Preview = preview,
                Date = Day.AddMinutes(-minutesBefore), UnreadCount = unread, TopMessageId = 100,
                ReadOutboxMaxId = 100, ReadInboxMaxId = unread > 0 ? 90 : 100,
            };
        }

        private static List<ChatEntry> DemoChats()
        {
            var ada = Entry(AdaId, new InputPeerUser(AdaId, 0), "Ada Lovelace",
                            "The installer warned and let me continue.", -10, 0);
            ada.MainPinOrder = 0;
            var testers = Entry(TestersId, new InputPeerChat(TestersId), "TelegArm Testers",
                                "Charles: build 2.0.0-alpha.1 is up", 35, 12);
            testers.IsGroup = true; testers.Muted = true;
            return new List<ChatEntry>
            {
                ada, testers,
                Entry(NotesId, new InputPeerChannel(NotesId, 0), "Release Notes",
                      "TelegArm 1.9.0: notifications and Action Center", 180, 1),
                Entry(SaraId, new InputPeerUser(SaraId, 0), "سارا رضایی",
                      "سلام! فردا می‌بینمت", 60 * 26, 3),
                Entry(WeekendId, new InputPeerUser(WeekendId, 0), "Weekend plans ☕",
                      "😀 see you there", 60 * 50, 0),
                Entry(CharlesId, new InputPeerUser(CharlesId, 0), "Charles Babbage",
                      "The difference engine builds 0/0", 60 * 24 * 9, 0),
            };
        }

        private static Message Msg(int id, bool outgoing, int minute, string text, int replyTo = 0)
        {
            var m = new Message { id = id, date = Day.AddMinutes(minute), message = text, peer_id = new PeerUser { user_id = AdaId } };
            if (outgoing) m.flags |= Message.Flags.out_;
            if (replyTo != 0) m.reply_to = new MessageReplyHeader { reply_to_msg_id = replyTo };
            return m;
        }

        private static List<Message> DemoMessages()
        {
            return new List<Message>
            {
                Msg(91, false, -60 * 24, "Morning! Did the new build install cleanly on the Surface?"),
                Msg(92, true, -60 * 24 + 3, "Yes. Shift+Enter makes a new line now, and the send key is a setting."),
                Msg(93, false, 1, "Persian check: سلام، حالت چطوره؟"),
                Msg(94, true, 2, "خوبم، ممنون! Mixed right-to-left and left-to-right in one bubble."),
                Msg(95, false, 4, "Emoji check 😀👍 ☕"),
                Msg(96, true, 6, "A longer message, to see how a bubble wraps when the text is wider than the column " +
                                 "it sits in. It should break between words and keep its time in the corner.", 91),
                Msg(97, false, 9, "Looks right to me."),
                Msg(98, true, 10, "The installer warned and let me continue."),
            };
        }

        private static void SeedChatList(Form main)
        {
            var all = (IList)Field(main, "_allChats");
            foreach (var c in DemoChats()) all.Add(c);
            Invoke(main, "RenderChatList", "");
            Settle(main);
        }

        /// <summary>The UI-only part of OpenChat + LoadHistoryAsync, with the network calls left out.</summary>
        private static void OpenDemoChat(Form main)
        {
            var entry = ((IEnumerable)Field(main, "_allChats")).Cast<ChatEntry>().First(c => c.PeerId == AdaId);
            SetField(main, "_selectedChat", entry);
            SetField(main, "_readOutboxMaxId", entry.ReadOutboxMaxId);
            foreach (var btn in new[] { "_chatSearchBtn", "_chatMenuBtn", "_dockBtn", "_callBtn" })
            {
                var c = Field(main, btn) as Control;
                if (c != null) c.Visible = true;
            }
            Invoke(main, "PlaceCallButton");   // as OpenChat does after showing the phone
            var row = Invoke(main, "FindChatItem", entry.PeerId);
            if (row != null)
            {
                SetField(main, "_selectedItem", row);
                row.GetType().GetProperty("Selected").SetValue(row, true, null);
            }
            Invoke(main, "ShowComposeFooter");
            ((Control)Field(main, "_chatTitle")).Text = entry.Title;
            Invoke(main, "SetHeaderAvatar", entry);

            var panel = (Control)Field(main, "_messagePanel");
            var shown = (IList)Field(main, "_currentChatMessages");
            panel.SuspendLayout();
            foreach (var m in DemoMessages())
            {
                Invoke(main, "AddMessageBubble", entry, m, null, null);
                shown.Add(m);
            }
            panel.ResumeLayout();
            Invoke(main, "ScrollMessagesToBottom");
            Settle(main);
        }

        private static Form NewThemedDialog()
        {
            var t = typeof(ThemedDialog).GetNestedType("Dialog", BindingFlags.NonPublic);
            if (t == null) throw new MissingMemberException("ThemedDialog", "Dialog");
            return (Form)Activator.CreateInstance(t, Inst, null,
                new object[] { "Delete chat", "Delete this chat for everyone? This can't be undone.", new[] { "Delete", "Cancel" } }, null);
        }

        private static void ShootMenu(string name)
        {
            ThemedContextMenuStrip menu = null;
            try
            {
                menu = new ThemedContextMenuStrip();
                foreach (var s in new[] { "Reply", "Copy text", "Forward", "Pin", "Select" }) menu.Items.Add(s);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Delete");
                menu.Prepare(null);   // step 2.0: what an opening does — icons, touch-sized rows (Restyled: at the DPI)
                menu.Size = menu.GetPreferredSize(Size.Empty);
                CreateControlIgnoringVisible.Invoke(menu, new object[] { true });
                Capture(menu, name);
            }
            catch (Exception ex) { Fail(name, ex); }
            finally { if (menu != null) menu.Dispose(); }
        }

        // ── reflection: every miss names the member, so a rename fails one shot, visibly ───────────────────

        private static FieldInfo FindField(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Inst | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static object Field(object o, string name)
        {
            var f = FindField(o.GetType(), name);
            if (f == null) throw new MissingFieldException(o.GetType().Name, name);
            return f.GetValue(o);
        }

        private static void SetField(object o, string name, object value)
        {
            var f = FindField(o.GetType(), name);
            if (f == null) throw new MissingFieldException(o.GetType().Name, name);
            f.SetValue(o, value);
        }

        private static object Invoke(object o, string name, params object[] args)
        {
            var m = o.GetType().GetMethods(Inst).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
            if (m == null) throw new MissingMethodException(o.GetType().Name, name + "/" + args.Length);
            try { return m.Invoke(o, args); }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
        }
    }
}
