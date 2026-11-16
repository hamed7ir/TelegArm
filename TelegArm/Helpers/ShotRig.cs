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
using System.Threading.Tasks;
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
            // Device trip 5: a second run into the same folder overwrote the first run's report (and only the failed run
            // came back). An earlier report is kept beside the new one, named by its own time.
            try
            {
                string old = Path.Combine(dir, "shots.txt");
                if (File.Exists(old))
                    File.Move(old, Path.Combine(dir, "shots-" + File.GetLastWriteTime(old).ToString("yyyyMMdd-HHmmss") + ".txt"));
            }
            catch { }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Fail("UI thread", e.Exception);
            Directory.CreateDirectory(dir);

            _log.Append(DiagReport.Build());
            _log.AppendLine("Isolated data root: " + isolatedRoot);
            _log.AppendLine();
            string live = null;
            try { live = Environment.GetEnvironmentVariable("TELEGARM_LIVE"); } catch { }
            Anim.Suppressed = true;   // every animation lands at once: a screenshot must never catch a popup half faded
            if (live == "1") return RunLive(dir);
            if (live == "placement") return RunLivePlacement(dir);
            if (live == "keys") return RunLiveKeys(dir);
            if (live == "columns") return RunLiveColumns(dir);
            if (live == "media") return RunLiveMedia(dir);
            if (live == "camera") return RunLiveCamera(dir);
            if (live == "callcam") return RunLiveCallCamera(dir);
            if (live == "calltest") return RunLiveCallTest(dir);
            if (live == "backdrop") return RunLiveBackdrop(dir);
            if (live == "teaser") return RunLiveTeaser(dir);
            if (live == "anim") return RunLiveAnim(dir);
            if (live == "resize") return RunLiveResize(dir);
            if (live == "startup") return RunLiveStartup(dir);
            if (live == "update") return RunLiveUpdate(dir);

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
                // 2026-10-16 video (drawn pictures, no camera): theirs (portrait) with ours (landscape, mirrored) in the corner and
                // the switch button; ours alone while it rings out; an incoming video call
                CallWindow.ShowSwitchForRig = true;
                var theirs = DemoCallPicture(480, 640, Color.SteelBlue, Color.SandyBrown);
                var ours = DemoCallPicture(640, 480, Color.SeaGreen, Color.Tan);
                var v1 = new Core.Calls.CallSession { Peer = ada, PeerId = 1001, Outgoing = true, Video = true, CameraOn = true, RemoteVideo = 2,
                                                      Phase = Core.Calls.CallPhase.Established, EstablishedUtc = DateTime.UtcNow.AddSeconds(-125), SignalBars = 4 };
                Feed(v1.RemoteFrames, theirs, 480, 640);
                Feed(v1.LocalVideo, ours, 640, 480);
                Shot("p8-call-video-" + t, () => new CallWindow(calls, v1, null, s => "Ada Lovelace"));
                var v2 = new Core.Calls.CallSession { Peer = ada, PeerId = 1001, Outgoing = true, Video = true, CameraOn = true, Phase = Core.Calls.CallPhase.Ringing };
                Feed(v2.LocalVideo, ours, 640, 480);
                Shot("p8-call-videoself-" + t, () => new CallWindow(calls, v2, null, s => "Ada Lovelace"));
                var v3 = new Core.Calls.CallSession { Peer = ada, PeerId = 1001, Outgoing = false, Video = true, CameraOn = true, Phase = Core.Calls.CallPhase.Incoming };
                Shot("p8-call-videoin-" + t, () => new CallWindow(calls, v3, null, s => "Ada Lovelace"));
                var v4 = new Core.Calls.CallSession { Peer = ada, PeerId = 1001, Outgoing = true, Phase = Core.Calls.CallPhase.Ringing,
                                                      CameraNote = "You can turn on video once the call is connected." };
                Shot("p8-call-camnote-" + t, () => new CallWindow(calls, v4, null, s => "Ada Lovelace"));
                CallWindow.ShowSwitchForRig = false;
                // 2026-10-16: the voice chat window (an offline GroupCallManager: nothing joins) - connected with six people (Ada
                // speaking, a raised hand, one muted by an admin, one with a bio); us muted by an admin while it connects; the end
                var gcalls = new Core.Calls.GroupCallManager(new TelegramService(), new WindowsFormsSynchronizationContext());
                Func<long, int, TL.GroupCallParticipant.Flags, TL.GroupCallParticipant> gp = (id, src, fl) =>
                    new TL.GroupCallParticipant { peer = new TL.PeerUser { user_id = id }, source = src, flags = fl, date = DateTime.UtcNow.AddMinutes(-id % 7) };
                var gnames = new Dictionary<long, string> { { 1000, "Hamed" }, { 1001, "Ada Lovelace" }, { 1002, "Charles Babbage" }, { 1003, "Mary Somerville" },
                                                            { 1004, "Augustus De Morgan" }, { 1005, "Luigi Menabrea" } };
                const TL.GroupCallParticipant.Flags Muted = TL.GroupCallParticipant.Flags.muted, CanUnmute = TL.GroupCallParticipant.Flags.can_self_unmute;
                Func<Core.Calls.GroupCallSession> people = () =>
                {
                    var gs = new Core.Calls.GroupCallSession { PeerId = 77, Title = "Analytical Engine club" };
                    var hand = gp(1003, 14, Muted | TL.GroupCallParticipant.Flags.has_raise_hand_rating); hand.raise_hand_rating = 5;
                    var bio = gp(1005, 16, Muted | CanUnmute | TL.GroupCallParticipant.Flags.has_about); bio.about = "Translator, Turin";
                    gs.Participants.Apply(new[] { gp(1000, 11, Muted | CanUnmute | TL.GroupCallParticipant.Flags.self), gp(1001, 12, 0), gp(1002, 13, Muted | CanUnmute),
                                                  hand, gp(1004, 15, Muted), bio }, (id, u) => gnames[id]);
                    gs.Participants.ServerCount = 6;
                    return gs;
                };
                var g1 = people();
                g1.Phase = Core.Calls.GroupCallPhase.Connected; g1.ConnectedUtc = DateTime.UtcNow.AddSeconds(-754); g1.Muted = false;
                var speaker = g1.Participants.BySource(12); speaker.Level = 0.7f; speaker.SpeakingUntil = Environment.TickCount + 600000;
                Shot("p8-groupcall-" + t, () => new GroupCallWindow(gcalls, g1, null));
                var g2 = people();
                g2.Phase = Core.Calls.GroupCallPhase.Connecting; g2.ForceMuted = true; g2.HandRaised = true;
                g2.Note = "An admin muted you: raise your hand to ask to speak.";
                Shot("p8-groupcall-forcemuted-" + t, () => new GroupCallWindow(gcalls, g2, null));
                var g3 = people();
                g3.Phase = Core.Calls.GroupCallPhase.Ended; g3.EndText = "The voice chat has ended";
                Shot("p8-groupcall-ended-" + t, () => new GroupCallWindow(gcalls, g3, null));
                Shot("p8-groupcall-icons-" + t, () => IconSheet(GroupCallWindow.VoiceChatPath, GroupCallWindow.MicPath, GroupCallWindow.MicOffPath, GroupCallWindow.HandPath));
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

        /// <summary>A stand-in video picture (BGRA): a background, a head and shoulders. Drawn, never a camera's.</summary>
        private static byte[] DemoCallPicture(int w, int h, Color back, Color skin)
        {
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, w, h), back, ColorMath.Mix(back, Color.Black, 0.5f), 90f))
                        g.FillRectangle(bg, 0, 0, w, h);
                    int head = Math.Min(w, h) / 3;
                    using (var b = new SolidBrush(ColorMath.Mix(back, Color.Black, 0.35f)))
                        g.FillEllipse(b, w / 2 - head, h * 6 / 10, head * 2, head * 2);   // shoulders
                    using (var b = new SolidBrush(skin))
                        g.FillEllipse(b, w / 2 - head / 2, h * 6 / 10 - head, head, head * 6 / 5);
                }
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var px = new byte[w * h * 4];
                    for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w * 4, w * 4);
                    return px;
                }
                finally { bmp.UnlockBits(data); }
            }
        }

        /// <summary>Hands a picture to a call's video sink the way tgcalls_native does (its callback, a native pointer).</summary>
        private static void Feed(Core.Calls.CallVideoSink sink, byte[] bgra, int w, int h)
        {
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { sink.Callback(IntPtr.Zero, pin.AddrOfPinnedObject(), w, h, w * 4); }
            finally { pin.Free(); }
        }

        /// <summary>TELEGARM_LIVE=callcam (2026-10-16): THIS machine's camera through the call camera, each backend (the
        /// Windows 10 frame reader, and the Windows 8.1 media sink forced on), then through two calls connected in this
        /// process (A sends it, B decodes it), then the other camera. Counts pictures and sizes only: no picture is saved.</summary>
        private static int RunLiveCallCamera(string dir)
        {
            var trace = new System.Diagnostics.TextWriterTraceListener(Path.Combine(dir, "trace.txt"));
            System.Diagnostics.Trace.Listeners.Add(trace);
            System.Diagnostics.Trace.AutoFlush = true;
            Logger.Enabled = true;
            Action<bool, string> expect = (ok, what) => { _log.AppendLine("  " + (ok ? "PASS  " : "FAIL  ") + what); if (!ok) _failures++; };
            expect(Core.Calls.TgCalls.Available, "tgcalls_native: " + Core.Calls.TgCalls.Status);
            foreach (string backend in new[] { "reader", "sink" })
            {
                _log.AppendLine("  -- " + backend);
                if (backend == "reader" && !Core.Camera.CameraCapture.FrameReaderPresent)
                {
                    _log.AppendLine("  info  no MediaFrameReader on this Windows (8.1): the media sink is the call camera here");
                    continue;
                }
                Environment.SetEnvironmentVariable("TELEGARM_CALL_CAMERA", backend);
                var preview = new Core.Calls.CallVideoSink();
                Core.Calls.CallCamera cam = null;
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    cam = RunSync(Core.Calls.CallCamera.OpenAsync(preview));
                    expect(cam != null, "opened in " + sw.ElapsedMilliseconds + " ms via " + (cam != null ? cam.Backend : "-") + " (" + (cam != null && cam.Current != null ? cam.Current.Name : "-") + ")");
                    if (cam == null) continue;
                    expect(cam.Backend == (backend == "reader" ? "frame reader" : "media sink"), "the backend asked for");
                    var s0 = cam.Stats();
                    int p0 = preview.Frames;
                    LivePump(3000);
                    var s1 = cam.Stats();
                    double fps = (s1.Pushed - s0.Pushed) / 3.0, pfps = (preview.Frames - p0) / 3.0;
                    expect(fps >= 12, "camera " + fps.ToString("0.0") + " pictures/s into the DLL over 3 s");
                    expect(pfps >= 8, "preview " + pfps.ToString("0.0") + "/s (capped at 15), " + preview.Store.FrontWidth + "x" + preview.Store.FrontHeight);
                    LoopbackCamera(cam.Handle, expect);
                    if (cam.Cameras.Count > 1)
                    {
                        bool ok = RunSync(cam.SwitchAsync()) == 1;
                        var t0 = cam.Stats();
                        LivePump(2000);
                        var t1 = cam.Stats();
                        double sfps = (t1.Pushed - t0.Pushed) / 2.0;
                        expect(ok && sfps >= 12, "switched to " + (cam.Current != null ? cam.Current.Name : "-") + ": " + sfps.ToString("0.0") + " pictures/s");
                    }
                }
                catch (Exception ex) { expect(false, backend + ": " + ex.GetType().Name + " " + ex.Message); }
                finally
                {
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    if (cam != null) cam.Dispose();
                    _log.AppendLine("  info  closed in " + w.ElapsedMilliseconds + " ms");
                    Environment.SetEnvironmentVariable("TELEGARM_CALL_CAMERA", null);
                }
            }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>TELEGARM_LIVE=backdrop (owner 2026-10-16: "the mini player, the pinned bar and the downloads button need the
        /// backdrop too"): the rail layout with a chat open, its pinned bar, the mini player and the downloads button shown,
        /// in both themes. Run with TELEGARM_WALLPAPER=&lt;picture&gt; to force the backdrop on.</summary>
        private static int RunLiveBackdrop(string dir)
        {
            MainForm.DemoNoSession = true;
            AppSettings.Instance.FolderSidebar = true;   // in memory only
            foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
            {
                ThemeHelper.InitMode(mode);
                MainForm f = null;
                try
                {
                    f = new MainForm(new TelegramService());
                    var wa = Screen.PrimaryScreen.WorkingArea;
                    f.StartPosition = FormStartPosition.Manual;
                    f.Bounds = new Rectangle(wa.Left + 60, wa.Top + 40, f.WindowDpiValue * 1000 / 96, f.WindowDpiValue * 700 / 96);
                    f.TopMost = true;
                    f.Show();
                    LivePump(1200);
                    SeedChatList(f);
                    OpenDemoChat(f);
                    LivePump(400);
                    SetField(f, "_pinnedMessages", new List<Message> { new Message { id = 77, message = "The pinned message", date = DateTime.UtcNow, peer_id = new PeerUser { user_id = AdaId } } });
                    SetField(f, "_pinnedChatId", AdaId);
                    Invoke(f, "UpdatePinnedBar");
                    var mini = (Control)Field(f, "_miniBar");
                    mini.Visible = true;
                    Invoke(f, "SetRowPx", Field(f, "_rightLayout"), 1, 48);
                    var dl = Field(f, "_dlIndicator") as Control;
                    if (dl != null) dl.Visible = true;
                    LivePump(800);
                    LiveShot(f, "backdrop-bars-" + (mode == ThemeMode.Dark ? "dark" : "light"));
                    _log.AppendLine("  backdrop " + (WindowBackdrop.Enabled ? "ON" : "off") + ": pinned " + ((Control)Field(f, "_pinnedBar")).Visible
                                    + ", mini " + mini.Visible + ", downloads " + (dl != null && dl.Visible));
                    string tn = mode == ThemeMode.Dark ? "dark" : "light";
                    // RT trip 2026-10-16 (owner): the hamburger drawer and the downloads list show it too
                    Invoke(f, "ShowDrawer");
                    LivePump(700);
                    LiveShot(f, "backdrop-drawer-" + tn);
                    Invoke(f, "CloseDrawer");
                    LivePump(300);
                    var svc = (TelegramService)Field(f, "_activeService");
                    var roster = (System.Collections.IDictionary)typeof(TelegramService).GetField("_roster", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(svc);
                    var handle = new DownloadHandle(5150, Path.Combine(_dir, "demo.mp4"));
                    handle.Pause();
                    roster[5150L] = new TelegramService.TransferInfo { DocId = 5150, FileName = "Engine lecture.mp4", ChatTitle = "Ada Lovelace", Path = Path.Combine(_dir, "demo.mp4"), Handle = handle, Type = "video" };
                    var panel = new DownloadsPanel(svc, mode == ThemeMode.Dark, ThemeHelper.Palette.Accent, f.WindowDpiValue);
                    panel.Location = f.PointToScreen(new Point(f.ClientSize.Width - panel.Width - 40, 80));
                    panel.Show(f);
                    Invoke(panel, "Rebuild");
                    LivePump(800);
                    using (var bmp = new Bitmap(panel.Width, panel.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(panel.Location, Point.Empty, panel.Size);
                        bmp.Save(Path.Combine(_dir, "backdrop-downloads-" + tn + ".png"), ImageFormat.Png);
                    }
                    panel.Close();
                    LivePump(300);
                }
                catch (Exception ex) { _log.AppendLine("  FAIL  " + ex.GetType().Name + " " + ex.Message); _failures++; }
                finally
                {
                    if (f != null) { try { f.Close(); } catch { } try { f.Dispose(); } catch { } }
                    LivePump(400);
                }
            }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>The teaser's voice chat: six people, one speaking, a raised hand, one muted by an admin.</summary>
        private static Core.Calls.GroupCallSession TeaserVoiceChat()
        {
            var names = new Dictionary<long, string> { { 1000, "Hamed" }, { 1001, "Ada Lovelace" }, { 1002, "Charles Babbage" }, { 1003, "Mary Somerville" },
                                                       { 1004, "Augustus De Morgan" }, { 1005, "Luigi Menabrea" } };
            Func<long, int, TL.GroupCallParticipant.Flags, TL.GroupCallParticipant> gp = (id, src, fl) =>
                new TL.GroupCallParticipant { peer = new TL.PeerUser { user_id = id }, source = src, flags = fl, date = DateTime.UtcNow.AddMinutes(-id % 7) };
            const TL.GroupCallParticipant.Flags Muted = TL.GroupCallParticipant.Flags.muted, CanUnmute = TL.GroupCallParticipant.Flags.can_self_unmute;
            var s = new Core.Calls.GroupCallSession { PeerId = 77, Title = "Analytical Engine club", Phase = Core.Calls.GroupCallPhase.Connected,
                                                      ConnectedUtc = DateTime.UtcNow.AddSeconds(-754), Muted = false };
            var hand = gp(1003, 14, Muted | TL.GroupCallParticipant.Flags.has_raise_hand_rating); hand.raise_hand_rating = 5;
            var bio = gp(1005, 16, Muted | CanUnmute | TL.GroupCallParticipant.Flags.has_about); bio.about = "Translator, Turin";
            s.Participants.Apply(new[] { gp(1000, 11, Muted | CanUnmute | TL.GroupCallParticipant.Flags.self), gp(1001, 12, 0), gp(1002, 13, Muted | CanUnmute),
                                         hand, gp(1004, 15, Muted), bio }, (id, u) => names[id]);
            s.Participants.ServerCount = 6;
            var speaker = s.Participants.BySource(12); speaker.Level = 0.7f; speaker.SpeakingUntil = Environment.TickCount + 600000;
            return s;
        }

        /// <summary>TELEGARM_LIVE=teaser (2026-10-16, the release teaser): curated scenes, demo data only, on a STAGE - a
        /// borderless window over the whole screen showing TELEGARM_WALLPAPER, so a capture never shows this PC's desktop.
        /// Each scene is one PNG of the screen (t-*.png); a compositor outside the app turns them into the GIF / MP4.</summary>
        private static int RunLiveTeaser(string dir)
        {
            MainForm.DemoNoSession = true;
            UI.Ringer.Silent = true;
            string wall = null;
            try { wall = Environment.GetEnvironmentVariable("TELEGARM_WALLPAPER"); } catch { }
            var scr = Screen.PrimaryScreen;
            var stage = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = scr.Bounds, ShowInTaskbar = false,
                BackColor = Color.Black, TopMost = true,
                BackgroundImage = wall != null && File.Exists(wall) ? Image.FromFile(wall) : null, BackgroundImageLayout = ImageLayout.Stretch,
            };
            stage.Show();
            LivePump(500);
            Action<string> snap = name =>
            {
                LivePump(700);
                using (var bmp = new Bitmap(scr.Bounds.Width, scr.Bounds.Height))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(scr.Bounds.Location, Point.Empty, scr.Bounds.Size);
                    bmp.Save(Path.Combine(_dir, name + ".png"), ImageFormat.Png);
                }
                _log.AppendLine("  scene " + name);
            };
            Func<Form, int, int> px = (form, v) => v * ((AppForm)form).WindowDpiValue / 96;
            var folders = new DialogFilterBase[]
            {
                null,
                new DialogFilter { id = 2, title = new TextWithEntities { text = "Work", entities = new MessageEntity[0] }, flags = DialogFilter.Flags.groups,
                                   pinned_peers = new InputPeer[0], include_peers = new InputPeer[0], exclude_peers = new InputPeer[0] },
                new DialogFilter { id = 3, title = new TextWithEntities { text = "Family", entities = new MessageEntity[0] }, flags = DialogFilter.Flags.contacts,
                                   pinned_peers = new InputPeer[0], include_peers = new InputPeer[0], exclude_peers = new InputPeer[0] },
                new DialogFilter { id = 4, title = new TextWithEntities { text = "Unread", entities = new MessageEntity[0] }, flags = DialogFilter.Flags.exclude_read,
                                   pinned_peers = new InputPeer[0], include_peers = new InputPeer[0], exclude_peers = new InputPeer[0] },
            };
            foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
            {
                string t = mode == ThemeMode.Dark ? "dark" : "light";
                ThemeHelper.InitMode(mode);
                AppSettings.Instance.FolderSidebar = true;   // in memory only
                MainForm f = null;
                try
                {
                    f = new MainForm(new TelegramService());
                    f.StartPosition = FormStartPosition.Manual;
                    f.TopMost = true;
                    f.Show();
                    LivePump(900);
                    int w = px(f, 1060), h = px(f, 690);
                    f.Bounds = new Rectangle(scr.Bounds.X + (scr.Bounds.Width - w) / 2, scr.Bounds.Y + (scr.Bounds.Height - h) / 2, w, h);
                    SeedChatList(f);
                    SetField(f, "_folders", folders);
                    Invoke(f, "RebuildFolders");
                    OpenDemoChat(f);
                    SetField(f, "_pinnedMessages", new List<Message> { new Message { id = 77, message = "Release day! TelegArm 2.0 is out.", date = DateTime.UtcNow, peer_id = new PeerUser { user_id = AdaId } } });
                    SetField(f, "_pinnedChatId", AdaId);
                    Invoke(f, "UpdatePinnedBar");
                    Invoke(f, "RefreshFolderBadges");
                    f.Activate();
                    snap("t-main-" + t);

                    if (mode == ThemeMode.Dark)
                    {
                        // the emoji picker over the composer
                        Invoke(f, "OpenEmojiPicker");
                        LivePump(900);
                        snap("t-emoji-" + t);
                        foreach (var pick in Application.OpenForms.OfType<EmojiPicker>().ToList()) { try { pick.Close(); } catch { } }
                        LivePump(300);

                        // a video call beside it (drawn pictures; no camera, no call)
                        CallWindow.ShowSwitchForRig = true;
                        var calls = new Core.Calls.CallManager(new TelegramService(), new WindowsFormsSynchronizationContext());
                        var ada = new TL.User { id = AdaId, first_name = "Ada", last_name = "Lovelace" };
                        var v = new Core.Calls.CallSession { Peer = ada, PeerId = AdaId, Outgoing = true, Video = true, CameraOn = true, RemoteVideo = 2,
                                                             Phase = Core.Calls.CallPhase.Established, EstablishedUtc = DateTime.UtcNow.AddSeconds(-94), SignalBars = 4 };
                        Feed(v.RemoteFrames, DemoCallPicture(480, 640, Color.SteelBlue, Color.SandyBrown), 480, 640);
                        Feed(v.LocalVideo, DemoCallPicture(640, 480, Color.SeaGreen, Color.Tan), 640, 480);
                        var cw = new CallWindow(calls, v, null, x => "Ada Lovelace") { StartPosition = FormStartPosition.Manual };
                        var centred = f.Location;
                        f.Left = scr.Bounds.X + px(f, 24);   // the main window left, the call on the right, both whole
                        cw.Show(f);
                        cw.Location = new Point(scr.Bounds.Right - cw.Width - px(f, 40), scr.Bounds.Y + (scr.Bounds.Height - cw.Height) / 2);
                        cw.Activate();
                        snap("t-call-" + t);
                        cw.Close();
                        CallWindow.ShowSwitchForRig = false;
                        LivePump(300);

                        // a voice chat beside it (demo people; nothing joins)
                        var gcalls = new Core.Calls.GroupCallManager(new TelegramService(), new WindowsFormsSynchronizationContext());
                        var vc = TeaserVoiceChat();
                        var gw = new GroupCallWindow(gcalls, vc, null) { StartPosition = FormStartPosition.Manual };
                        gw.Show(f);
                        gw.Location = new Point(scr.Bounds.Right - gw.Width - px(f, 40), scr.Bounds.Y + (scr.Bounds.Height - gw.Height) / 2);
                        gw.Activate();
                        snap("t-voicechat-" + t);
                        gw.Close();
                        f.Location = centred;
                        LivePump(300);

                        // Settings, the Appearance page, over the main window ("Wallpaper" chosen, as the stage shows it)
                        AppSettings.Instance.WindowBackground = "wallpaper";   // in memory only (isolated data root)
                        var st = new SettingsForm { StartPosition = FormStartPosition.Manual, TopMost = true };
                        st.Show(f);
                        Invoke(st, "ShowPage", 1);
                        st.Location = new Point(f.Left + (f.Width - st.Width) / 2, f.Top + (f.Height - st.Height) / 2);
                        st.Activate();
                        snap("t-settings-" + t);
                        st.Close();
                        LivePump(300);
                    }
                    else
                    {
                        // one column: the chat alone, the folder rail beside it
                        f.Bounds = new Rectangle(scr.Bounds.X + (scr.Bounds.Width - px(f, 560)) / 2, f.Top, px(f, 560), h);
                        LivePump(600);
                        Invoke(f, "ShowColumn", false);
                        snap("t-onecol-" + t);
                    }
                }
                catch (Exception ex) { _log.AppendLine("  FAIL  " + ex.GetType().Name + " " + ex.Message + " " + ex.StackTrace); _failures++; }
                finally
                {
                    if (f != null) { try { f.Close(); } catch { } try { f.Dispose(); } catch { } }
                    LivePump(400);
                }
            }
            stage.Close();
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>TELEGARM_LIVE=calltest (2026-10-16, the device trip): the call library's own self-tests in THIS process's
        /// architecture (on the Surface: the first ARM32 run of tgcalls) - the voice loopback, then the video loopback on the
        /// newest library and on the old one, with the codec this machine sends (VP8 on ARM32), and the CPU it took. No
        /// account, no server, no camera (a drawn test picture).</summary>
        private static int RunLiveCallTest(string dir)
        {
            var trace = new System.Diagnostics.TextWriterTraceListener(Path.Combine(dir, "trace.txt"));
            System.Diagnostics.Trace.Listeners.Add(trace);
            System.Diagnostics.Trace.AutoFlush = true;
            Logger.Enabled = true;
            Action<bool, string> expect = (ok, what) => { _log.AppendLine("  " + (ok ? "PASS  " : "FAIL  ") + what); if (!ok) _failures++; };
            bool loaded = Core.Calls.TgCalls.Available;
            expect(loaded, "tgcalls_native: " + Core.Calls.TgCalls.Status);
            if (loaded)
            {
                var me = System.Diagnostics.Process.GetCurrentProcess();
                Func<string, Func<byte[], int>, bool> run = (name, test) =>
                {
                    var report = new byte[8192];
                    me.Refresh();
                    var cpu0 = me.TotalProcessorTime;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int rc = -1;
                    try { rc = test(report); }
                    catch (Exception ex) { _log.AppendLine("  info  " + ex.GetType().Name + ": " + ex.Message); }
                    me.Refresh();
                    double cpu = (me.TotalProcessorTime - cpu0).TotalMilliseconds;
                    foreach (var line in Core.Calls.TgCalls.Z(report).Trim().Split('\n')) _log.AppendLine("        " + line);
                    expect(rc == 0, name + " (" + sw.ElapsedMilliseconds + " ms, CPU " + cpu.ToString("0") + " ms = " +
                           (cpu / Math.Max(1, sw.ElapsedMilliseconds) / Environment.ProcessorCount * 100).ToString("0") + "% of " + Environment.ProcessorCount + " cores)");
                    return rc == 0;
                };
                run("voice loopback", b => Core.Calls.TgCalls.tgn_selftest_loopback(25, b, b.Length));
                // the speaker path the real calls use (the trip: "their voice was choppy and late"): B plays A's tone through
                // this device's speaker at 1/10 volume for 10 s, the report counts the gaps
                run("the speaker, 10 s (a quiet tone)", b => Core.Calls.TgCalls.tgn_selftest_audio(Core.Calls.TgCalls.AudioSpeakerTest, 10, b, b.Length));
                run("video loopback, newest library", b => Core.Calls.TgCalls.tgn_selftest_video(null, 30, 0, b, b.Length));
                run("video loopback, library 5.0.0", b => Core.Calls.TgCalls.tgn_selftest_video(System.Text.Encoding.UTF8.GetBytes("5.0.0\0"), 30, 0, b, b.Length));
                // voice chats: the group engine (join payload, levels, our tone once unmuted, rejoin; no server)
                if (Core.Calls.TgCalls.GroupAvailable) run("voice chat engine", b => Core.Calls.TgCalls.tgn_selftest_group(0, b, b.Length));
                else _log.AppendLine("  FAIL  voice chat engine: the call library has no group calls");
                _log.AppendLine("  info  encoders made: " + Core.Calls.TgCalls.Encoders());
            }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>Two calls in this process (test tones), A sending <paramref name="camera"/>: B must decode its pictures.</summary>
        private static void LoopbackCamera(IntPtr camera, Action<bool, string> expect)
        {
            var key = new byte[256];
            new Random(5).NextBytes(key);
            string version = Core.Calls.TgCalls.Versions().Split(';')[0];
            var inA = new List<byte[]>();
            var inB = new List<byte[]>();
            int stA = -1, stB = -1, video = -1;
            var remote = new Core.Calls.CallVideoSink();
            Core.Calls.NativeCall a = null, b = null;
            try
            {
                a = Core.Calls.NativeCall.Create(version, key, true, null, null, true, 92, null, Core.Calls.TgCalls.AudioTest, 440, camera, n =>
                {
                    n.SignalingEmitted += d => { lock (inB) inB.Add(d); };
                    n.StateChanged += x => stA = x;
                });
                b = Core.Calls.NativeCall.Create(version, key, false, null, null, true, 92, null, Core.Calls.TgCalls.AudioTest, 1000, wire: n =>
                {
                    n.SignalingEmitted += d => { lock (inA) inA.Add(d); };
                    n.StateChanged += x => stB = x;
                    n.RemoteMediaChanged += (au, v) => video = v;
                });
                b.SetRemoteVideo(remote, 640, 640);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int f0 = -1;
                long t0 = 0;
                while (sw.ElapsedMilliseconds < 15000)
                {
                    byte[][] forA, forB;
                    lock (inA) { forA = inA.ToArray(); inA.Clear(); }
                    lock (inB) { forB = inB.ToArray(); inB.Clear(); }
                    foreach (var d in forA) a.ReceiveSignaling(d);
                    foreach (var d in forB) b.ReceiveSignaling(d);
                    if (f0 < 0 && remote.Frames > 0) { f0 = remote.Frames; t0 = sw.ElapsedMilliseconds; }
                    if (f0 >= 0 && sw.ElapsedMilliseconds - t0 >= 3000) break;
                    LivePump(20);
                }
                double fps = f0 < 0 ? 0 : (remote.Frames - f0) / ((sw.ElapsedMilliseconds - t0) / 1000.0);
                var vs = b.VideoStats();
                expect(stA == 2 && stB == 2 && video == 2 && fps >= 10,
                       "through a call: states " + stA + "/" + stB + ", remote video " + video + ", " + fps.ToString("0.0") + " pictures/s decoded (" +
                       vs.RemoteWidth + "x" + vs.RemoteHeight + "), first after " + t0 + " ms; encoders " + Core.Calls.TgCalls.Encoders());
                a.Stop(5000);
                b.Stop(5000);
            }
            finally
            {
                if (a != null) a.Dispose();
                if (b != null) b.Dispose();
            }
            GC.KeepAlive(remote);
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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetQueueStatus(uint flags);

        /// <summary>Pumps until nothing is waiting in the queue (input, posted, paint: QS_ALLINPUT minus timers) or the limit.</summary>
        private static void PumpUntilIdle(int limitMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < limitMs)
            {
                Application.DoEvents();
                if (((GetQueueStatus(0x04FF) >> 16) & ~0x0010u) == 0) break;   // 0x10 = QS_TIMER: periodic timers never stop
                System.Threading.Thread.Sleep(1);
            }
        }

        /// <summary>A tiny HTTP/1.1 server on 127.0.0.1 for the update rig: GET path -> (content type, bytes); unknown -> 404.
        /// Bodies over 64 KB go out in slices with a short pause, so a download shows its progress.</summary>
        private sealed class TinyHttp
        {
            private readonly System.Net.Sockets.TcpListener _l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            private readonly Dictionary<string, KeyValuePair<string, byte[]>> _routes = new Dictionary<string, KeyValuePair<string, byte[]>>();
            private volatile bool _stop;
            public int Port { get; private set; }
            public int Requests;
            public void Set(string path, string type, byte[] body) { lock (_routes) _routes[path] = new KeyValuePair<string, byte[]>(type, body); }
            public void Remove(string path) { lock (_routes) _routes.Remove(path); }
            public void Start()
            {
                _l.Start();
                Port = ((System.Net.IPEndPoint)_l.LocalEndpoint).Port;
                new System.Threading.Thread(() =>
                {
                    while (!_stop)
                    {
                        System.Net.Sockets.TcpClient c;
                        try { c = _l.AcceptTcpClient(); } catch { break; }
                        var t = new System.Threading.Thread(() => Serve(c)) { IsBackground = true };
                        t.Start();
                    }
                }) { IsBackground = true }.Start();
            }
            public void Stop() { _stop = true; try { _l.Stop(); } catch { } }
            private void Serve(System.Net.Sockets.TcpClient c)
            {
                try
                {
                    using (c)
                    using (var s = c.GetStream())
                    {
                        var rd = new StreamReader(s, Encoding.ASCII);
                        string first = rd.ReadLine() ?? "";
                        string line;
                        while (!string.IsNullOrEmpty(line = rd.ReadLine())) { }
                        System.Threading.Interlocked.Increment(ref Requests);
                        var parts = first.Split(' ');
                        string path = parts.Length > 1 ? parts[1] : "/";
                        KeyValuePair<string, byte[]> r;
                        bool found;
                        lock (_routes) found = _routes.TryGetValue(path, out r);
                        byte[] body = found ? r.Value : Encoding.ASCII.GetBytes("not found");
                        string head = "HTTP/1.1 " + (found ? "200 OK" : "404 Not Found") + "\r\nContent-Type: " + (found ? r.Key : "text/plain")
                                    + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
                        var hb = Encoding.ASCII.GetBytes(head);
                        s.Write(hb, 0, hb.Length);
                        int slice = body.Length > 65536 ? body.Length / 20 : body.Length;
                        for (int o = 0; o < body.Length; o += slice)
                        {
                            s.Write(body, o, Math.Min(slice, body.Length - o));
                            if (slice < body.Length) System.Threading.Thread.Sleep(15);
                        }
                    }
                }
                catch { }
            }
        }

        private static string Sha256Hex(byte[] b)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return string.Concat(sha.ComputeHash(b).Select(x => x.ToString("x2")));
        }

        /// <summary>TELEGARM_LIVE=update (owner 2026-11-15: "check the repo for updates in background and warn user there is a
        /// update, and when user clicks it download the update and install"). A fake GitHub on 127.0.0.1: nothing newer =
        /// no pill; a newer release = the Update pill in the title bar; the download shows progress, is checked against its
        /// SHA256SUMS.txt and starts the installer in update mode (caught, not run); a damaged download or a release with no
        /// checksums is refused and nothing starts; a dead server stays silent. TELEGARM_UPDATE_REAL=1 also asks the real
        /// GitHub (info). TELEGARM_SETUP=&lt;Setup.exe&gt; also runs its update mode as a dry run (files only), waiting for a
        /// TelegArm process to end first.</summary>
        private static int RunLiveUpdate(string dir)
        {
            ThemeHelper.InitMode(ThemeMode.Light);
            MainForm.DemoNoSession = true;
            Anim.Suppressed = true;
            var srv = new TinyHttp();
            MainForm f = null;
            try
            {
                srv.Start();
                string root = "http://127.0.0.1:" + srv.Port;
                Environment.SetEnvironmentVariable("TELEGARM_UPDATE_URL", root + "/latest");
                Environment.SetEnvironmentVariable("TELEGARM_UPDATE_PREFIX", root + "/download/");
                var rnd = new Random(5);
                var setup = new byte[1200000];
                rnd.NextBytes(setup);
                setup[0] = (byte)'M'; setup[1] = (byte)'Z';
                string good = Sha256Hex(setup);
                Func<string, bool, string> release = (tag, withSums) =>
                    "{\"tag_name\":\"" + tag + "\",\"draft\":false,\"prerelease\":false,\"html_url\":\"https://github.com/hamed7ir/TelegArm/releases/tag/" + tag
                    + "\",\"body\":\"Test notes\",\"assets\":[{\"name\":\"TelegArm-9.9.9-Setup.exe\",\"size\":" + setup.Length
                    + ",\"browser_download_url\":\"" + root + "/download/v9.9.9/TelegArm-9.9.9-Setup.exe\"}"
                    + (withSums ? ",{\"name\":\"SHA256SUMS.txt\",\"size\":100,\"browser_download_url\":\"" + root + "/download/v9.9.9/SHA256SUMS.txt\"}" : "") + "]}";
                srv.Set("/download/v9.9.9/TelegArm-9.9.9-Setup.exe", "application/octet-stream", setup);
                srv.Set("/download/v9.9.9/SHA256SUMS.txt", "text/plain", Encoding.ASCII.GetBytes("TelegArm 9.9.9\n\n" + good + "  TelegArm-9.9.9-Setup.exe\n"));

                f = new MainForm(new TelegramService());
                var wa = Screen.PrimaryScreen.WorkingArea;
                f.StartPosition = FormStartPosition.Manual;
                f.Bounds = new Rectangle(wa.Left + 60, wa.Top + 60, Math.Min(wa.Width - 120, 1000 * f.WindowDpiValue / 96), Math.Min(wa.Height - 120, 640 * f.WindowDpiValue / 96));
                f.TopMost = true;
                f.Show();
                LivePump(1200);
                Action<Task> wait = t => { var sw = System.Diagnostics.Stopwatch.StartNew(); while (!t.IsCompleted && sw.ElapsedMilliseconds < 30000) { Application.DoEvents(); System.Threading.Thread.Sleep(5); } };
                Func<UI.Controls.UpdatePill> pill = () => Field(f, "_updatePill") as UI.Controls.UpdatePill;
                Action<bool, string> check = (ok, what) => { _log.AppendLine("  " + (ok ? "PASS  " : "FAIL  ") + what); if (!ok) _failures++; };

                // 1. the newest release is this build: no pill
                srv.Set("/latest", "application/json", Encoding.UTF8.GetBytes(release("v" + Program.Version, true)));
                wait((Task)Invoke(f, "CheckForUpdateAsync", false));
                check(pill() == null || !pill().Visible, "the newest release is this version (" + Program.Version + "): no Update pill");

                // 2. a newer one: the pill in the title bar, left of the proxy pill
                srv.Set("/latest", "application/json", Encoding.UTF8.GetBytes(release("v9.9.9", true)));
                wait((Task)Invoke(f, "CheckForUpdateAsync", false));
                LivePump(400);
                var bar = (UI.Controls.TitleBar)Member(f, "Bar");
                var up = pill();
                var proxy = Field(f, "_proxyPill") as Control;
                bool inBar = up != null && up.Visible && ReferenceEquals(bar.LeadSlot, up) && up.Parent == bar && up.Bottom <= bar.Height && up.Top >= 0
                             && (proxy == null || !proxy.Visible || up.Right <= proxy.Left);
                check(inBar, "a newer release (9.9.9): the Update pill shows in the title bar, left of the proxy pill (pill " + (up != null ? up.Bounds.ToString() : "none")
                      + ", proxy " + (proxy != null && proxy.Visible ? proxy.Bounds.ToString() : "hidden") + ", bar height " + bar.Height + ")");
                var shotArea = bar.RectangleToScreen(bar.ClientRectangle);
                using (var bmp = new Bitmap(shotArea.Width, shotArea.Height))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(shotArea.Location, Point.Empty, shotArea.Size);
                    bmp.Save(Path.Combine(_dir, "update-pill.png"), ImageFormat.Png);
                }

                // 3. the download: progress in the pill, checked against SHA256SUMS.txt, the installer started in update mode
                string installDir = Path.Combine(_dir, "fake-install");
                Directory.CreateDirectory(installDir);
                Updater.TestInstallDir = installDir;
                var launched = new List<string>();
                Updater.LaunchOverride = (path, args) => launched.Add(path + "|" + args);
                var seen = new List<int>();
                var rel = Updater.Pending;
                var task = (Task<string>)Invoke(f, "RunUpdateAsync", rel, installDir);
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted && sw2.ElapsedMilliseconds < 30000)
                {
                    Application.DoEvents();
                    int pr = up.Progress;
                    if (seen.Count == 0 || seen[seen.Count - 1] != pr) seen.Add(pr);
                    if (pr >= 40 && pr <= 70 && !File.Exists(Path.Combine(_dir, "update-pill-progress.png")))
                        using (var bmp = new Bitmap(shotArea.Width, shotArea.Height))
                        {
                            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(shotArea.Location, Point.Empty, shotArea.Size);
                            bmp.Save(Path.Combine(_dir, "update-pill-progress.png"), ImageFormat.Png);
                        }
                    System.Threading.Thread.Sleep(2);
                }
                string err3 = task.IsCompleted ? task.Result : "timed out";
                string setupPath = launched.Count > 0 ? launched[0].Split('|')[0] : null;
                bool intact = setupPath != null && File.Exists(setupPath) && Sha256Hex(File.ReadAllBytes(setupPath)) == good;
                string expectArgs = "--update \"" + installDir + "\" --wait " + System.Diagnostics.Process.GetCurrentProcess().Id;
                bool argsOk = launched.Count == 1 && launched[0].EndsWith("|" + expectArgs);
                check(err3 == null && intact && argsOk && seen.Count >= 4 && seen[seen.Count - 1] == 100,
                      "Update: the installer downloads (progress " + string.Join(",", seen.Take(12)) + (seen.Count > 12 ? ",…" : "") + "), matches its SHA256SUMS.txt and starts as \""
                      + (launched.Count > 0 ? launched[0].Split('|')[1] : "nothing") + "\"" + (err3 != null ? " (error: " + err3 + ")" : ""));

                // 4. a damaged download: refused, nothing starts, the pill is back to "Update"
                srv.Set("/download/v9.9.9/SHA256SUMS.txt", "text/plain", Encoding.ASCII.GetBytes(new string('0', 64) + "  TelegArm-9.9.9-Setup.exe\n"));
                launched.Clear();
                var t4 = (Task<string>)Invoke(f, "RunUpdateAsync", rel, installDir);
                wait(t4);
                check(t4.Result != null && t4.Result.Contains("damaged") && launched.Count == 0 && up.Progress == -1,
                      "a download that does not match its checksum is refused and nothing starts (\"" + t4.Result + "\")");

                // 5. a release with no SHA256SUMS.txt: refused before downloading anything
                srv.Set("/latest", "application/json", Encoding.UTF8.GetBytes(release("v9.9.9", false)));
                wait((Task)Invoke(f, "CheckForUpdateAsync", false));
                var t5 = (Task<string>)Invoke(f, "RunUpdateAsync", Updater.Pending, installDir);
                wait(t5);
                check(t5.Result != null && t5.Result.Contains("SHA256SUMS") && launched.Count == 0,
                      "a release without SHA256SUMS.txt is refused (\"" + t5.Result + "\")");

                // 6. GitHub out of reach: the background check says nothing (no dialog, no exception)
                srv.Stop();
                var t6 = (Task)Invoke(f, "CheckForUpdateAsync", false);
                wait(t6);
                check(t6.IsCompleted && !t6.IsFaulted, "GitHub out of reach: the background check stays silent");

                // 7. the real GitHub (info only)
                if (Environment.GetEnvironmentVariable("TELEGARM_UPDATE_REAL") == "1")
                {
                    Environment.SetEnvironmentVariable("TELEGARM_UPDATE_URL", null);
                    Environment.SetEnvironmentVariable("TELEGARM_UPDATE_PREFIX", null);
                    var tr = Updater.CheckAsync();
                    wait(tr);
                    if (tr.IsFaulted) _log.AppendLine("  info  the real GitHub: " + tr.Exception.GetBaseException().GetType().Name + ": " + tr.Exception.GetBaseException().Message);
                    else _log.AppendLine("  info  the real GitHub answered: " + (tr.Result == null ? "nothing newer than " + Program.Version : "newer " + tr.Result.Tag
                        + ", installer " + tr.Result.SetupName + ", checksums " + (tr.Result.SumsUrl != null)));
                }

                // 8. the installer's update mode, as a dry run: it waits for a TelegArm process to end, then installs the files
                string setupExe = Environment.GetEnvironmentVariable("TELEGARM_SETUP");
                if (!string.IsNullOrEmpty(setupExe) && File.Exists(setupExe))
                {
                    string target = Path.Combine(_dir, "dryrun-install");
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    var other = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--shoot \"" + Path.Combine(_dir, "waited-for") + "\"") { UseShellExecute = false };
                    other.EnvironmentVariables["TELEGARM_LIVE"] = "startup";
                    other.EnvironmentVariables.Remove("TELEGARM_SETUP");
                    var waited = System.Diagnostics.Process.Start(other);
                    var psi = new System.Diagnostics.ProcessStartInfo(setupExe, "--update \"" + target + "\" --wait " + waited.Id) { UseShellExecute = false };
                    psi.EnvironmentVariables["TELEGARM_SETUP_DRYRUN"] = "1";
                    var sw8 = System.Diagnostics.Stopwatch.StartNew();
                    var inst = System.Diagnostics.Process.Start(psi);
                    while (!inst.HasExited && sw8.ElapsedMilliseconds < 240000) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                    bool exited = inst.HasExited;
                    int code = exited ? inst.ExitCode : -1;
                    // the installer's own log says whom it waited for and when (the Process object's view was unreliable here)
                    string ulog = Path.Combine(Path.GetTempPath(), "TelegArm-update", "update.log");
                    string logText = File.Exists(ulog) ? File.ReadAllText(ulog) : "";
                    int waitedAt = logText.LastIndexOf("pid " + waited.Id + " is TelegArm, it ended after", StringComparison.Ordinal);
                    bool otherDoneFirst = waitedAt >= 0 && logText.IndexOf("installed", waitedAt, StringComparison.Ordinal) > waitedAt;
                    bool files = File.Exists(Path.Combine(target, "TelegArm.exe")) && File.Exists(Path.Combine(target, "uninstall.manifest"));
                    check(exited && code == 0 && files && otherDoneFirst, "the installer's update mode (dry run) waits for TelegArm to end, then installs (exit " + code
                          + ", files " + files + ", the waited-for TelegArm ended first " + otherDoneFirst + ", " + (sw8.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s)");
                    try { if (!inst.HasExited) inst.Kill(); } catch { }
                }
            }
            catch (Exception ex) { Fail("live-update", ex); }
            finally
            {
                srv.Stop();
                Updater.LaunchOverride = null;
                Updater.TestInstallDir = null;
                if (f != null) { try { f.Close(); } catch { } try { f.Dispose(); } catch { } }
            }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>TELEGARM_LIVE=startup (owner 2026-11-15, the PGO question): how long a start takes, from the process
        /// starting to the main window shown and idle, then to the chat list and a chat drawn. Run it several times with
        /// TELEGARM_JITPROFILE_DIR=&lt;one folder&gt; (multicore JIT: the first run records, later ones use it) and with
        /// TELEGARM_MCJ=0 to compare.</summary>
        private static int RunLiveStartup(string dir)
        {
            Func<double> sinceStart = () => (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            double atRig = sinceStart();
            ThemeHelper.InitMode(ThemeMode.Light);
            MainForm.DemoNoSession = true;
            MainForm f = null;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                f = new MainForm(new TelegramService());
                double ctor = sw.Elapsed.TotalMilliseconds;
                var wa = Screen.PrimaryScreen.WorkingArea;
                f.StartPosition = FormStartPosition.Manual;
                f.Bounds = new Rectangle(wa.Left + 40, wa.Top + 40, Math.Min(wa.Width - 80, 1100 * f.WindowDpiValue / 96), Math.Min(wa.Height - 80, 700 * f.WindowDpiValue / 96));
                sw.Restart();
                f.Show();
                PumpUntilIdle(20000);
                double show = sw.Elapsed.TotalMilliseconds, shownAt = sinceStart();
                sw.Restart();
                SeedChatList(f);
                OpenDemoChat(f);
                PumpUntilIdle(20000);
                double chat = sw.Elapsed.TotalMilliseconds;
                _log.AppendLine("  info  startup: multicore JIT " + (Program.JitProfileOn ? "on, " + (Program.JitProfileExisted ? "with a profile from an earlier start" : "recording (no profile yet)") : "off")
                    + "; the rig began " + atRig.ToString("0") + " ms after the process started; the main window built in " + ctor.ToString("0") + " ms, shown and idle "
                    + show.ToString("0") + " ms later (" + shownAt.ToString("0") + " ms after the start); the chat list and a chat drawn in " + chat.ToString("0") + " ms more");
                LivePump(1500);   // let the profile writer see the end of start-up
            }
            catch (Exception ex) { Fail("live-startup", ex); }
            finally { if (f != null) { try { f.Close(); } catch { } try { f.Dispose(); } catch { } } }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>TELEGARM_LIVE=resize (owner 2026-11-13: "resizing windows should become smooth"): a chat of 200 more
        /// messages, the window dragged narrower and wider in steps as a live resize (WM_ENTERSIZEMOVE .. WM_EXITSIZEMOVE),
        /// every step timed: the synchronous part (layout and measure) and the paints after it. Then the end state is
        /// compared with a full re-measure at the same width (a resize must end exactly where a plain layout would), and
        /// the reading position must have been kept: the bottom stays the bottom, a message in the middle stays in view.</summary>
        private static int RunLiveResize(string dir)
        {
            ThemeHelper.InitMode(ThemeMode.Light);
            MainForm.DemoNoSession = true;
            if (Environment.GetEnvironmentVariable("TELEGARM_COMPOSITED") == "0") AppSettings.Instance.CompositedScroll = false;   // A/B, in memory
            MainForm f = null;
            try
            {
                f = new MainForm(new TelegramService());
                var wa = Screen.PrimaryScreen.WorkingArea;
                f.StartPosition = FormStartPosition.Manual;
                int dpi = f.WindowDpiValue;
                f.Bounds = new Rectangle(wa.Left + 40, wa.Top + 40, Math.Min(wa.Width - 80, 1100 * dpi / 96), Math.Min(wa.Height - 80, 720 * dpi / 96));
                f.TopMost = true;
                f.Show();
                LivePump(1200);
                SeedChatList(f);
                OpenDemoChat(f);
                LivePump(400);
                var chatMsgs = (IList)Field(f, "_currentChatMessages");
                var panel = (ScrollableControl)Field(f, "_messagePanel");
                var rnd = new Random(7);
                string[] words = "the window gets narrower and every bubble has to wrap its text again which is the cost of a live resize on a slow tablet so this chat is long".Split(' ');
                int extra = 200;   // TELEGARM_RESIZE_N=50: a chat as it is first loaded
                try { int v; if (int.TryParse(Environment.GetEnvironmentVariable("TELEGARM_RESIZE_N"), out v) && v > 0) extra = v; } catch { }
                panel.SuspendLayout();
                for (int i = 0; i < extra; i++)
                {
                    int n = 2 + rnd.Next(i % 9 == 0 ? 90 : 26);
                    var sb = new StringBuilder();
                    for (int k = 0; k < n; k++) sb.Append(words[rnd.Next(words.Length)]).Append(' ');
                    string text = i % 11 == 5 ? "\u0633\u0644\u0627\u0645\u060C \u0627\u06CC\u0646 \u06CC\u06A9 \u067E\u06CC\u0627\u0645 \u0641\u0627\u0631\u0633\u06CC \u0627\u0633\u062A \u06A9\u0647 \u0628\u0627\u06CC\u062F \u062F\u0631 \u0686\u0646\u062F \u062E\u0637 \u0634\u06A9\u0633\u062A\u0647 \u0634\u0648\u062F \u0648\u0642\u062A\u06CC \u067E\u0646\u062C\u0631\u0647 \u0628\u0627\u0631\u06CC\u06A9 \u0645\u06CC\u200C\u0634\u0648\u062F"
                                              : sb.ToString().Trim();
                    var m = new TL.Message
                    {
                        id = 20000 + i, date = DateTime.UtcNow.AddMinutes(-400 + i), peer_id = new TL.PeerUser { user_id = AdaId }, message = text,
                        flags = i % 3 == 0 ? TL.Message.Flags.out_ : 0,
                    };
                    chatMsgs.Add(m);
                    Invoke(f, "AddMessageBubble", Field(f, "_selectedChat"), m, null, null);
                }
                panel.ResumeLayout(true);
                Invoke(f, "ScrollMessagesToBottom");
                LivePump(800);
                int count = panel.Controls.Count;
                _log.AppendLine("  " + count + " bubbles in the chat, window " + f.Width + "x" + f.Height + " at " + dpi + " dpi");

                Func<List<double>, string> stat = v => v.Count == 0 ? "-" : v.Average().ToString("0.0") + " ms avg, " + v.Max().ToString("0.0") + " ms worst";
                Func<int> bottomGap = () => panel.DisplayRectangle.Height + panel.AutoScrollPosition.Y - panel.ClientSize.Height;   // 0 = at the very bottom
                // one drag: 25 steps narrower, 15 wider. Each step: the synchronous part (WM_SIZE: layout and measure), then
                // the paints it caused.
                int midGap = 0, midStale = 0, midReadingTop = 0, catchUps = 0;
                Control watched = null;   // the message being read (the middle-of-the-chat drag sets it)
                // The window as the drag left it against the same window fully repainted: a pixel that differs is a stale
                // one (an old border, a bubble at its old place). The caret of the composer blinks: its box is ignored.
                var stale = new List<string>();
                Action<string> sameAsRepaint = when =>
                {
                    // the pointer away from the window: a window moving under a still pointer gets mouse moves, and a button's
                    // hover changing between the two captures is not a stale pixel
                    // (not a corner: those are hot on Windows 8.1 (charms, app switcher) and 11 (show desktop))
                    var scr = Screen.FromControl(f).Bounds;
                    Cursor.Position = new Point(scr.Left + Math.Max(2, Math.Min(20, f.Left - scr.Left - 4)), scr.Top + scr.Height / 2);
                    LivePump(150);
                    var r = f.Bounds;
                    r.Intersect(Screen.FromControl(f).Bounds);
                    using (var asDragged = new Bitmap(r.Width, r.Height))
                    using (var repainted = new Bitmap(r.Width, r.Height))
                    {
                        using (var g = Graphics.FromImage(asDragged)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                        f.Invalidate(true);
                        LivePump(400);
                        using (var g = Graphics.FromImage(repainted)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                        var inputBox = Field(f, "_messageInput"); var box = inputBox == null ? null : inputBox.GetType().GetProperty("Control").GetValue(inputBox, null) as Control;
                        var ignore = box != null && box.IsHandleCreated ? box.RectangleToScreen(box.ClientRectangle) : Rectangle.Empty;
                        ignore.Offset(-r.X, -r.Y);
                        // the floating date pill fades out on its own timer (seen on the Surface): its box is ignored too
                        var pill = Field(f, "_dateFlyout") as Control;
                        var pillBox = pill != null && pill.IsHandleCreated ? pill.RectangleToScreen(pill.ClientRectangle) : Rectangle.Empty;
                        pillBox.Offset(-r.X, -r.Y);
                        pillBox.Inflate(2, 2);
                        int n = 0; Rectangle bad = Rectangle.Empty;
                        for (int y = 0; y < r.Height; y++)
                            for (int x = 0; x < r.Width; x++)
                            {
                                if (ignore.Contains(x, y) || pillBox.Contains(x, y)) continue;
                                Color a = asDragged.GetPixel(x, y), b = repainted.GetPixel(x, y);
                                if (Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B))) <= 8) continue;
                                n++;
                                bad = bad.IsEmpty ? new Rectangle(x, y, 1, 1) : Rectangle.Union(bad, new Rectangle(x, y, 1, 1));
                            }
                        if (n > 0)
                        {
                            stale.Add(when + ": " + n + " px in " + bad);
                            asDragged.Save(Path.Combine(_dir, "resize-stale-" + when + "-as-dragged.png"), ImageFormat.Png);
                            repainted.Save(Path.Combine(_dir, "resize-stale-" + when + "-repainted.png"), ImageFormat.Png);
                        }
                    }
                };
                Func<int, string> drag = stepPx =>
                {
                    var sync = new List<double>(); var paint = new List<double>();
                    int w0 = f.Width;
                    var sw = new System.Diagnostics.Stopwatch();
                    SendMessage(f.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero);   // WM_ENTERSIZEMOVE
                    for (int pass = 0; pass < 2; pass++)
                        for (int i = 0; i < (pass == 0 ? 25 : 15); i++)   // 25 steps narrower, 15 wider: it ends at ANOTHER width than it began
                        {
                            int w = pass == 0 ? w0 - (i + 1) * stepPx : w0 - 25 * stepPx + (i + 1) * stepPx;
                            sw.Restart();
                            f.Width = w;
                            sync.Add(sw.Elapsed.TotalMilliseconds);
                            sw.Restart();
                            Application.DoEvents();
                            paint.Add(sw.Elapsed.TotalMilliseconds);
                            if (pass == 0 && i == 24)   // the narrowest point, still dragging
                            {
                                midGap = bottomGap();
                                midStale = panel.Controls.OfType<UI.Controls.MessageBubbleControl>().Count(b => b.MeasuredAtWidth != b.Width);
                                midReadingTop = watched != null ? watched.Top : 0;
                                LiveShot(f, "resize-narrowest-" + (watched == null ? "bottom" : "middle"));
                                sameAsRepaint("narrowest-" + (watched == null ? "bottom" : "middle"));
                            }
                        }
                    sw.Restart();
                    SendMessage(f.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero);   // WM_EXITSIZEMOVE
                    double exit = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    LivePump(700);   // anything deferred to the end of the drag
                    sameAsRepaint("after-" + (watched == null ? "bottom" : "middle"));
                    int ups = (int)f.GetType().GetProperty("DragCatchUps", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f, null);
                    string held = ups > catchUps ? "; content held, caught up " + (ups - catchUps) + " times" : "; live";
                    catchUps = ups;
                    return "frame " + stat(sync) + "; paint " + stat(paint) + "; per step " + (sync.Average() + paint.Average()).ToString("0.0") + " ms; the end of the drag " + exit.ToString("0.0") + " ms" + held;
                };
                Func<List<Rectangle>> boundsNow = () => panel.Controls.Cast<Control>().Select(c => c.Bounds).ToList();

                // TELEGARM_PROF=1: where a drag step spends its time. A second thread stops this one every few ms and reads its
                // stack (dev rig only: Thread.Suspend is what it takes); a method's count = samples with it anywhere on the stack.
                if (Environment.GetEnvironmentVariable("TELEGARM_PROF") == "1")
                {
                    var ui = System.Threading.Thread.CurrentThread;
                    var hits = new Dictionary<string, int>();
                    var leafSync = new Dictionary<string, int>(); var leafPaint = new Dictionary<string, int>();
                    int samples = 0; bool stop = false;
                    var sampler = new System.Threading.Thread(() =>
                    {
                        while (!stop)
                        {
                            System.Diagnostics.StackTrace st = null;
#pragma warning disable 618
                            try { ui.Suspend(); st = new System.Diagnostics.StackTrace(ui, false); }
                            catch { }
                            finally { try { ui.Resume(); } catch { } }
#pragma warning restore 618
                            if (st != null)
                            {
                                samples++;
                                var seen = new HashSet<string>();
                                var names = new List<string>();
                                foreach (var fr in st.GetFrames() ?? new System.Diagnostics.StackFrame[0])
                                {
                                    var mb = fr.GetMethod();
                                    if (mb == null || mb.DeclaringType == null) continue;
                                    string name = mb.DeclaringType.Name + "." + mb.Name;
                                    names.Add(name);
                                    if (seen.Add(name)) { int n; hits.TryGetValue(name, out n); hits[name] = n + 1; }
                                }
                                // the innermost 4 frames, for the samples inside the resize (sync) and inside a paint
                                string leaf = string.Join(" < ", names.Take(4));
                                var into = seen.Contains("Form.SetBoundsCore") ? leafSync : seen.Contains("Control.WmPaint") ? leafPaint : null;
                                if (into != null) { int n; into.TryGetValue(leaf, out n); into[leaf] = n + 1; }
                            }
                            System.Threading.Thread.Sleep(1);
                        }
                    }) { IsBackground = true };
                    sampler.Start();
                    string prof = drag(Dpi.Px(12, dpi));
                    stop = true; sampler.Join(2000);
                    _log.AppendLine("  info  profiled drag: " + prof + "; " + samples + " samples");
                    foreach (var kv in hits.OrderByDescending(k => k.Value).Take(40))
                        _log.AppendLine("    " + (100.0 * kv.Value / Math.Max(1, samples)).ToString("0.0").PadLeft(5) + "%  " + kv.Key);
                    _log.AppendLine("  info  innermost frames while RESIZING (" + leafSync.Values.Sum() + " samples):");
                    foreach (var kv in leafSync.OrderByDescending(k => k.Value).Take(28)) _log.AppendLine("    " + kv.Value.ToString().PadLeft(4) + "  " + kv.Key);
                    _log.AppendLine("  info  innermost frames while PAINTING (" + leafPaint.Values.Sum() + " samples):");
                    foreach (var kv in leafPaint.OrderByDescending(k => k.Value).Take(22)) _log.AppendLine("    " + kv.Value.ToString().PadLeft(4) + "  " + kv.Key);
                    File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
                    return 0;
                }

                // at the bottom of the chat (where a chat is read): it must stay the bottom
                string atBottom = drag(Dpi.Px(12, dpi));
                int gapAfter = bottomGap();
                _log.AppendLine("  info  drag at the bottom: " + atBottom);
                // the end state against a plain full layout at the same width
                var after = boundsNow();
                int scrollAfter = panel.AutoScrollPosition.Y;
                foreach (Control c in panel.Controls) { var b = c as UI.Controls.MessageBubbleControl; if (b != null) b.Measure(); }
                panel.PerformLayout();
                panel.AutoScrollPosition = new Point(0, -scrollAfter);
                var reference = boundsNow();
                int differ = 0;
                for (int i = 0; i < after.Count; i++) if (after[i] != reference[i]) differ++;
                bool same = differ == 0;
                _log.AppendLine("  " + (same ? "PASS  " : "FAIL  ") + "after the drag every bubble is exactly where a plain layout puts it (" + differ + " of " + after.Count + " differ)");
                if (!same) _failures++;
                bool stayed = gapAfter <= Dpi.Px(4, dpi) && midGap <= Dpi.Px(4, dpi);
                _log.AppendLine("  " + (stayed ? "PASS  " : "FAIL  ") + "the bottom of the chat stays the bottom through the drag (" + midGap + " px from it at the narrowest point, "
                    + gapAfter + " px after; " + midStale + " of " + count + " bubbles waited for the end of the drag)");
                if (!stayed) _failures++;
                LiveShot(f, "resize-after-bottom");

                // in the middle of the chat: the message being read stays in view
                panel.AutoScrollPosition = new Point(0, panel.DisplayRectangle.Height / 2);
                LivePump(300);
                var reading = panel.Controls.Cast<Control>().Where(c => c.Bottom > Dpi.Px(8, dpi)).OrderBy(c => c.Top).First();
                int readingTop = reading.Top;
                watched = reading;
                string inMiddle = drag(Dpi.Px(12, dpi));
                _log.AppendLine("  info  drag in the middle: " + inMiddle);
                int moved = Math.Abs(reading.Top - readingTop), movedMid = Math.Abs(midReadingTop - readingTop);
                bool kept = moved <= Dpi.Px(2, dpi) && movedMid <= Dpi.Px(2, dpi);
                _log.AppendLine("  " + (kept ? "PASS  " : "FAIL  ") + "in the middle of the chat the message at the top of the view stays there (it moved " + movedMid
                    + " px at the narrowest point, " + moved + " px after; " + midStale + " bubbles waited)");
                if (!kept) _failures++;
                var after2 = boundsNow();
                int scroll2 = panel.AutoScrollPosition.Y;
                foreach (Control c in panel.Controls) { var b = c as UI.Controls.MessageBubbleControl; if (b != null) b.Measure(); }
                panel.PerformLayout();
                panel.AutoScrollPosition = new Point(0, -scroll2);
                var reference2 = boundsNow();
                int differ2 = 0;
                for (int i = 0; i < after2.Count; i++) if (after2[i] != reference2[i]) differ2++;
                _log.AppendLine("  " + (differ2 == 0 ? "PASS  " : "FAIL  ") + "and again every bubble is where a plain layout puts it (" + differ2 + " differ)");
                if (differ2 != 0) _failures++;

                bool clean = stale.Count == 0;
                _log.AppendLine("  " + (clean ? "PASS  " : "FAIL  ") + "what a drag leaves on the screen equals a full repaint, mid-drag and after it"
                    + (clean ? "" : " (" + string.Join("; ", stale) + ")"));
                if (!clean) _failures++;

                // a resize with no drag around it (maximise, snap, a DPI change): done in full at once
                int wNow = f.Width;
                f.Width = wNow - Dpi.Px(90, dpi);
                var plain = boundsNow();
                foreach (Control c in panel.Controls) { var b = c as UI.Controls.MessageBubbleControl; if (b != null) b.Measure(); }
                panel.PerformLayout();
                var plainRef = boundsNow();
                int differ3 = 0;
                for (int i = 0; i < plain.Count; i++) if (plain[i].Size != plainRef[i].Size) differ3++;
                _log.AppendLine("  " + (differ3 == 0 ? "PASS  " : "FAIL  ") + "a resize outside a drag measures every bubble at once (" + differ3 + " sizes differ)");
                if (differ3 != 0) _failures++;
                f.Width = wNow;
                LivePump(300);
            }
            catch (Exception ex) { Fail("live-resize", ex); }
            finally { if (f != null) { try { f.Close(); } catch { } try { f.Dispose(); } catch { } } }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>TELEGARM_LIVE=anim (owner 2026-11-13, animations): the ONE rig where the animations really run (every
        /// other rig suppresses them), sampled value by value: a switch slides, a chat row's hover fades in and out, the
        /// drawer slides in and out as one frame, a menu and the popups fade in; the clock stops when nothing moves; and each
        /// off switch makes its animation instant. Frame times are the clock's own. TELEGARM_ARM32=1 runs it at the
        /// Surface's frame interval (not its speed).</summary>
        private static int RunLiveAnim(string dir)
        {
            ThemeHelper.InitMode(ThemeMode.Light);
            MainForm.DemoNoSession = true;
            MainForm f = null;
            SettingsForm st = null;
            try
            {
                f = new MainForm(new TelegramService());
                var wa = Screen.PrimaryScreen.WorkingArea;
                f.StartPosition = FormStartPosition.Manual;
                f.Bounds = new Rectangle(wa.Left + wa.Width / 10, wa.Top + wa.Height / 12, wa.Width * 7 / 10, wa.Height * 3 / 4);
                f.TopMost = true;
                // trip 5: the main window of one run closed before the first step and nothing said why
                bool rigDone = false;
                f.FormClosing += (s, e) => { if (!rigDone) _log.AppendLine("  NOTE  the main window is closing before the rig ended: " + e.CloseReason
                    + (e.CloseReason == CloseReason.UserClosing ? " (a tap on its X, Alt+F4, or the taskbar)" : "")); };
                f.Show();
                Action<int> settle = ms => LivePump(Platform.IsArm32 ? ms * 3 : ms);   // the Surface needs longer to come to rest
                settle(1200);
                SeedChatList(f);
                OpenDemoChat(f);
                settle(600);

                Anim.Suppressed = false;
                bool winOn = Anim.SystemAnimationsOn;
                _log.AppendLine("  frame interval " + Anim.FrameMs + " ms" + (Platform.IsArm32 ? " (the ARM32 rate)" : "")
                                + "; Windows' own animations setting: " + (winOn ? "on" : "OFF"));
                // Windows' setting off (this dev box, 2026-11-13): nothing moves until "Also when Windows has animations off"
                // is on. Proven first, then turned on (in memory: the data root is the rig's own) so the rest can run.
                if (!winOn)
                {
                    bool held = !Anim.Enabled(AnimKind.Hover) && !Anim.Enabled(AnimKind.Popups);
                    AppSettings.Instance.AnimateDespiteSystem = true;
                    bool released = Anim.Enabled(AnimKind.Hover) && Anim.Enabled(AnimKind.Popups);
                    _log.AppendLine("  " + (held && released ? "PASS  " : "FAIL  ") + "Windows has animations off: nothing runs, until \"Also when Windows has animations off\" is on");
                    if (!(held && released)) _failures++;
                }
                // pumps messages for ms with NO sleep (every timer tick is taken when it is due) and records each new value
                Func<int, Func<double>, List<double>> watch = (ms, read) =>
                {
                    if (ms >= 200 && Platform.IsArm32) ms *= 3;   // the Surface: every wait for an animation to END is longer
                    var seen = new List<double>();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < ms)
                    {
                        Application.DoEvents();
                        double v = read();
                        if (seen.Count == 0 || !seen[seen.Count - 1].Equals(v)) seen.Add(v);
                    }
                    return seen;
                };
                Func<List<double>, int, bool> oneWay = (s, sign) => { for (int i = 1; i < s.Count; i++) if ((s[i] - s[i - 1]) * sign < 0) return false; return true; };
                Func<string> clock = () =>
                {
                    int n; float total, worst;
                    Anim.ReadStats(out n, out total, out worst);
                    return n + " frames, " + (n > 0 ? (total / n).ToString("0.0") : "-") + " ms each, longest " + worst.ToString("0.0") + " ms [" + Anim.GapsText() + "]"
                           + (Anim.LateStarts > 0 ? "; STARTED TOO LATE, SHOWN AT ONCE (" + Anim.LateStarts + ")" : "");
                };
                // A slow device: an animation whose first frame came very late is shown at once (by design), or runs in one
                // or two frames. That is a measurement to bring back, not a failure of the code.
                Func<bool> slowStart = () =>
                {
                    int n; float total, worst;
                    Anim.ReadStats(out n, out total, out worst);
                    return Anim.LateStarts > 0 || worst > 150f;
                };
                Action<bool, string> check = (ok, what) => { _log.AppendLine("  " + (ok ? "PASS  " : "FAIL  ") + what); if (!ok) _failures++; };
                Func<List<double>, string> ends = s => s.Count + " values, " + s[0].ToString("0.00") + " to " + s[s.Count - 1].ToString("0.00");
                int few = Platform.IsArm32 ? 3 : 4;   // the fewest values a 90-120 ms animation must be seen at (half the frames at the ARM rate)
                // a fade that ends visible: either it rose from nothing, or its first frame came too late and it was shown at once
                Func<List<double>, bool> fadeEndsVisible = v => v[v.Count - 1] >= 0.99 && oneWay(v, 1)
                    && ((v[0] <= 0.05 && (v.Count >= few || Anim.LateStarts > 0)) || Anim.LateStarts > 0);
                // Off the UI thread (2026-11-16): a window-sized capture took ~150 ms on the Surface, and a slide being measured
                // stood still for it (the "long frame" in the middle of the drawer and the column slides).
                var grabs = new List<System.Threading.Tasks.Task>();
                Action<Rectangle, string> grab = (area, name) =>
                {
                    area.Intersect(Screen.FromControl(f).Bounds);
                    if (area.Width <= 0 || area.Height <= 0) return;
                    string path = Path.Combine(_dir, name + ".png");
                    grabs.Add(System.Threading.Tasks.Task.Run(() =>
                    {
                        using (var bmp = new Bitmap(area.Width, area.Height))
                        {
                            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                            bmp.Save(path, ImageFormat.Png);
                        }
                    }));
                };

                // 1. a switch slides (Settings, Appearance, the Animations section)
                st = new SettingsForm { StartPosition = FormStartPosition.Manual, TopMost = true };
                st.Show(f);
                st.Location = new Point(f.Left + (f.Width - st.Width) / 2, f.Top + (f.Height - st.Height) / 2);
                Invoke(st, "ShowPage", 1);
                settle(500);
                var toggles = new List<UI.Controls.ToggleRow>();
                Action<Control> findT = null;
                findT = c => { foreach (Control k in c.Controls) { var tr = k as UI.Controls.ToggleRow; if (tr != null) toggles.Add(tr); findT(k); } };
                findT(st);
                var master = toggles.FirstOrDefault(t => t.Text == "Animations");
                var sw1 = toggles.FirstOrDefault(t => t.Text == "Buttons and rows fade on hover");
                check(master != null && sw1 != null && toggles.Count(t => t.Text == "Switches slide" || t.Text == "The menu slides in" || t.Text == "Popups and menus fade in") == 3,
                      "Settings, Appearance has the Animations switch and one switch per animation");
                for (Control c = sw1; c != null; c = c.Parent)
                {
                    var sc = c as ScrollableControl;
                    if (sc != null && sc.AutoScroll) { sc.ScrollControlIntoView(sw1); break; }
                }
                settle(300);
                var pos = (Fade)Field(sw1, "_pos");
                Anim.ResetStats();
                sw1.On = false;
                var offS = watch(450, () => pos.T);
                bool slidOff = !sw1.On && (offS.Count >= few || slowStart()) && offS[0] > 0.5 && offS[offS.Count - 1] == 0 && oneWay(offS, -1) && Anim.Active == 0;
                string switchClock = clock();
                sw1.On = true;
                watch(Anim.ToggleMs / 2, () => pos.T);
                grab(sw1.RectangleToScreen(sw1.ClientRectangle), "anim-switch-midway");
                var onS = watch(450, () => pos.T);
                check(slidOff && sw1.On && onS[onS.Count - 1] == 1 && oneWay(onS, 1),
                      "a switch slides: off in " + ends(offS) + "; " + switchClock + "; the clock stopped after it: " + (Anim.Active == 0));
                // its own off switch: the knob is there at once
                AppSettings.Instance.AnimateToggles = false;
                sw1.On = false;
                bool instant = pos.T == 0 && Anim.Active == 0;
                AppSettings.Instance.AnimateToggles = true;
                sw1.On = true;
                watch(400, () => pos.T);
                check(instant, "\"Switches slide\" off: the knob jumps, nothing on the clock");
                // the master switch dims the four (and they come back)
                master.On = false;
                bool dimmed = !sw1.Enabled && !Anim.Enabled(AnimKind.Hover) && !Anim.Enabled(AnimKind.Popups);
                master.On = true;
                check(dimmed && sw1.Enabled && Anim.Enabled(AnimKind.Hover), "the master switch turns every animation off, and the four switches grey out with it");
                st.Close();
                st = null;
                settle(300);

                // 2. a chat row under the mouse: the tint fades in and out
                var rows = new List<UI.Controls.ChatListItemControl>();
                Action<Control> findR = null;
                findR = c => { foreach (Control k in c.Controls) { var r = k as UI.Controls.ChatListItemControl; if (r != null && r.Visible) rows.Add(r); findR(k); } };
                findR(f);
                var row = rows.FirstOrDefault(r => !r.Selected);
                Anim.ResetStats();
                Invoke(row, "OnMouseEnter", EventArgs.Empty);
                var hov = (Fade)Field(row, "_hover");
                var up = watch(260, () => hov.T);
                grab(row.RectangleToScreen(row.ClientRectangle), "anim-row-hover");
                Invoke(row, "OnMouseLeave", EventArgs.Empty);
                var dn = watch(380, () => hov.T);
                check((slowStart() || (up.Count >= few - 1 && dn.Count >= few - 1)) && up[up.Count - 1] == 1 && oneWay(up, 1) && dn[dn.Count - 1] == 0 && oneWay(dn, -1) && Anim.Active == 0,
                      "a chat row's hover fades in (" + ends(up) + ") and out (" + ends(dn) + "); " + clock());

                // 3. the drawer slides in as one frame, lands, and slides away. 2026-11-16: its picture is drawn ahead of time (a
                // few seconds after start; here on demand) and reused by the open, and it stays on it when it lands
                var ahead = System.Diagnostics.Stopwatch.StartNew();
                Invoke(f, "PrepareDrawerPicture");
                long aheadMs = ahead.ElapsedMilliseconds;
                try { var pic = Field(f, "_drawerPic") as Bitmap; if (pic != null) pic.Save(Path.Combine(_dir, "anim-drawer-picture.png"), ImageFormat.Png); } catch { }   // what the slide shows
                int hits0 = (int)Field(f, "DrawerPicHits");
                Anim.ResetStats();
                var open = System.Diagnostics.Stopwatch.StartNew();
                Invoke(f, "ShowDrawer");
                long openMs = open.ElapsedMilliseconds;
                bool reused = (int)Field(f, "DrawerPicHits") == hits0 + 1;
                var drawer = (Control)Field(f, "_drawer");
                int dw = drawer.Width, x0 = drawer.Left;   // before any message is pumped: fully off the left edge
                bool oneFrame = Field(drawer, "_frame") != null;
                var xs = new List<double> { drawer.Left };
                EventHandler moved = (o, e2) => { if (!drawer.IsDisposed && !xs[xs.Count - 1].Equals((double)drawer.Left)) xs.Add(drawer.Left); };
                drawer.LocationChanged += moved;
                watch(Anim.DrawerInMs / 2, () => drawer.Left);
                grab(f.Bounds, "anim-drawer-midway");
                watch(500, () => drawer.Left);
                drawer.LocationChanged -= moved;
                bool sliding = (bool)drawer.GetType().GetProperty("Sliding").GetValue(drawer, null);
                bool kept = Field(drawer, "_frame") != null;   // no full repaint at landing: it rests on its picture
                bool landed = drawer.Left == 0 && !sliding && Anim.Active == 0;
                check(oneFrame && x0 == -dw && landed && kept && reused && oneWay(xs, 1) && (xs.Count >= few || slowStart()),
                      "the drawer slides in: " + xs.Count + " positions from " + x0 + " to " + xs[xs.Count - 1] + " px (its width " + dw + "), one picture (" + oneFrame
                      + "), reused from the one drawn ahead (" + reused + ", that took " + aheadMs + " ms), landed (" + landed + ": left " + drawer.Left + ", moving "
                      + Anim.Active + ") on its picture (" + kept + "); " + clock() + "; the open call took " + openMs + " ms");
                settle(200);
                grab(f.Bounds, "anim-drawer-open");
                Anim.ResetStats();
                var outX = new List<double> { drawer.Left };
                EventHandler movedOut = (o, e2) => { if (!drawer.IsDisposed && !outX[outX.Count - 1].Equals((double)drawer.Left)) outX.Add(drawer.Left); };
                drawer.LocationChanged += movedOut;
                Invoke(f, "CloseDrawer", true);
                bool detached = Field(f, "_drawer") == null;
                watch(450, () => drawer.IsDisposed ? -99999 : drawer.Left);
                check(detached && drawer.IsDisposed && (outX.Count >= few || slowStart()) && oneWay(outX, -1),
                      "the drawer slides away: closed at once for the app (" + detached + "), " + (outX.Count - 1) + " positions, then disposed; " + clock());
                // an action row closes it at once (a dialog follows): no slide. (The second open reuses the picture too.)
                var open2 = System.Diagnostics.Stopwatch.StartNew();
                Invoke(f, "ShowDrawer");
                long open2Ms = open2.ElapsedMilliseconds;
                watch(400, () => 0);
                var d2 = (Control)Field(f, "_drawer");
                Invoke(f, "CloseDrawer");
                check(d2.IsDisposed && Field(f, "_drawer") == null, "a close that an action follows is immediate (that open took " + open2Ms + " ms)");
                // the drawer's own switch off: it is simply there
                AppSettings.Instance.AnimateDrawer = false;
                Invoke(f, "ShowDrawer");
                var d3 = (Control)Field(f, "_drawer");
                bool there = d3.Left == 0 && !(bool)d3.GetType().GetProperty("Sliding").GetValue(d3, null) && Anim.Active == 0;
                Invoke(f, "CloseDrawer", true);
                check(there && d3.IsDisposed, "\"The menu slides in\" off: the drawer opens and closes at once");
                AppSettings.Instance.AnimateDrawer = true;
                settle(200);

                // 4. a menu fades in
                var menu = new ThemedContextMenuStrip();
                foreach (var label in new[] { "Reply", "Copy text", "Forward", "Delete" }) menu.Items.Add(new ToolStripMenuItem(label));
                Anim.ResetStats();
                menu.ShowAt(f, new Point(f.ClientSize.Width / 2, f.ClientSize.Height / 3));
                var mo = watch(Anim.MenuMs / 2, () => menu.Opacity);
                var mArea = menu.Bounds; mArea.Inflate(12, 12);
                grab(mArea, "anim-menu-midway");
                mo.AddRange(watch(350, () => menu.Opacity));
                check(mo[0] <= 0.05 && mo[mo.Count - 1] >= 0.99 && oneWay(mo, 1) && (mo.Count >= few || slowStart()) && Anim.Active == 0,
                      "a menu fades in: opacity " + ends(mo) + "; " + clock());
                // at rest after the fade (it stays a layered window): kept, to compare with the same menu opened unfaded
                var ring = menu.Bounds; ring.Inflate(14, 14);
                ring.Intersect(Screen.FromControl(f).Bounds);
                var faded = new Bitmap(ring.Width, ring.Height);
                using (var g = Graphics.FromImage(faded)) g.CopyFromScreen(ring.Location, Point.Empty, ring.Size);
                var menuAt = new Rectangle(menu.Left - ring.Left, menu.Top - ring.Top, menu.Width, menu.Height);
                menu.Close();
                settle(200);
                AppSettings.Instance.AnimatePopups = false;
                menu.ShowAt(f, new Point(f.ClientSize.Width / 2, f.ClientSize.Height / 3));
                settle(150);
                bool solid = menu.Opacity >= 0.999 && Anim.Active == 0;
                int inside = 0, around = 0, maxDiff = 0;
                using (var plain = new Bitmap(ring.Width, ring.Height))
                {
                    using (var g = Graphics.FromImage(plain)) g.CopyFromScreen(ring.Location, Point.Empty, ring.Size);
                    for (int y = 0; y < ring.Height; y++)
                        for (int x = 0; x < ring.Width; x++)
                        {
                            Color a = faded.GetPixel(x, y), b = plain.GetPixel(x, y);
                            int d = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                            if (d > maxDiff) maxDiff = d;
                            if (d > 3) { if (menuAt.Contains(x, y)) inside++; else around++; }
                        }
                    faded.Save(Path.Combine(_dir, "anim-menu-rest-faded.png"), ImageFormat.Png);
                    plain.Save(Path.Combine(_dir, "anim-menu-rest-plain.png"), ImageFormat.Png);
                }
                faded.Dispose();
                menu.Close();
                menu.Dispose();
                AppSettings.Instance.AnimatePopups = true;
                check(solid, "\"Popups and menus fade in\" off: the same menu opens solid (it was left layered by the fade before)");
                if (inside == 0 && around > 0 && Platform.WindowsMajor == 6)
                    _log.AppendLine("  info  at rest a faded menu equals an unfaded one inside; around it Windows 8.1 draws a shadow only for the faded one ("
                        + around + " px; known, cosmetic)");
                else
                check(inside == 0 && around == 0, "at rest a faded menu (still a layered window) looks like an unfaded one, its shadow included ("
                      + inside + " pixels differ inside it, " + around + " around it, the largest difference " + maxDiff + " of 255)");

                // 5. the popups fade in: the emoji picker, the downloads list, the reaction grid
                Anim.ResetStats();
                Invoke(f, "OpenEmojiPicker");
                var picker = Application.OpenForms.OfType<EmojiPicker>().FirstOrDefault();
                var po = watch(Anim.PopupMs / 2, () => picker.Opacity);
                var pArea = picker.Bounds; pArea.Inflate(12, 12);
                grab(pArea, "anim-picker-midway");
                po.AddRange(watch(450, () => picker.Opacity));
                check(fadeEndsVisible(po), "the emoji picker fades in: opacity " + ends(po) + "; " + clock());
                grab(pArea, "anim-picker-shown");
                picker.Close();
                settle(300);
                Anim.ResetStats();
                Invoke(f, "OpenDownloadsPanel");
                var panel = Application.OpenForms.OfType<DownloadsPanel>().FirstOrDefault();
                var dlo = watch(500, () => panel.Opacity);
                check(fadeEndsVisible(dlo), "the downloads list fades in: opacity " + ends(dlo) + "; " + clock());
                panel.Close();
                settle(300);
                var svc = Field(f, "_activeService");
                svc.GetType().GetField("_availableReactions", BindingFlags.NonPublic | BindingFlags.Instance)
                   .SetValue(svc, new[] { "\U0001F44D", "\U0001F44E", "\u2764", "\U0001F525", "\U0001F389", "\U0001F622", "\U0001F914", "\U0001F44F" });
                var lastMsg = ((IEnumerable)Field(f, "_currentChatMessages")).Cast<TL.Message>().Last();
                // the downloads list just closed: let the main window take the focus back first (a drop-down opened while
                // the activation is still moving closes at once; the grid was seen closed that way, twice)
                try { f.Activate(); } catch { }
                settle(500);
                Anim.ResetStats();
                Invoke(f, "ShowReactionGrid", lastMsg);
                var grid = Field(f, "_reactionGrid") as ToolStripDropDown;
                var go = watch(450, () => grid.Opacity);
                check(fadeEndsVisible(go), "the reaction grid fades in: opacity " + ends(go) + "; " + clock()
                      + " (still moving " + Anim.Active + ", grid visible " + grid.Visible + ", disposed " + grid.IsDisposed + ", same grid " + ReferenceEquals(grid, Field(f, "_reactionGrid")) + ")");
                Invoke(f, "CloseReactionGrid");
                settle(300);

                // 5a. stage 2 leftovers: a header button's hover tint fades; a menu row's highlight fades from row to row
                var hdr = (Button)Field(f, "_chatSearchBtn");
                var hdrFade = ((IDictionary)Field(f, "_headerHover"))[hdr] as Fade;
                bool nativeOff = hdr.FlatAppearance.MouseOverBackColor.A == 0 && hdr.FlatAppearance.MouseDownBackColor.A == 0;
                Anim.ResetStats();
                Invoke(hdr, "OnMouseEnter", EventArgs.Empty);
                var hIn = watch(260, () => hdrFade.T);
                Invoke(hdr, "OnMouseLeave", EventArgs.Empty);
                var hOut = watch(380, () => hdrFade.T);
                check(nativeOff && hIn[hIn.Count - 1] == 1 && oneWay(hIn, 1) && hOut[hOut.Count - 1] == 0 && oneWay(hOut, -1) && (hIn.Count >= few - 1 || slowStart()),
                      "a header button's hover tint fades in (" + ends(hIn) + ") and out (" + ends(hOut) + "); Windows' own instant tint is off: " + nativeOff + "; " + clock());
                var hlMenu = new ThemedContextMenuStrip();
                var rowA = new ToolStripMenuItem("Reply"); var rowB = new ToolStripMenuItem("Forward");
                hlMenu.Items.Add(rowA); hlMenu.Items.Add(rowB);
                hlMenu.ShowAt(f, new Point(f.ClientSize.Width / 2, f.ClientSize.Height / 3));
                settle(400);
                var table = typeof(ThemedMenuRenderer).GetField("_menuHover", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                Func<ToolStripItem, double> rowT = it =>
                {
                    var args = new object[] { it, null };
                    bool got = (bool)table.GetType().GetMethod("TryGetValue").Invoke(table, args);
                    return got ? ((Fade)args[1]).T : -1;
                };
                Anim.ResetStats();
                rowA.Select();
                var aIn = watch(300, () => rowT(rowA));
                rowB.Select();
                var both = new List<string>();
                var bIn = watch(400, () => { both.Add(rowT(rowA).ToString("0.00") + "/" + rowT(rowB).ToString("0.00")); return rowT(rowB); });
                double aAfter = rowT(rowA);
                hlMenu.Close(); hlMenu.Dispose();
                check(aIn[aIn.Count - 1] == 1 && oneWay(aIn, 1) && bIn[bIn.Count - 1] == 1 && oneWay(bIn, 1) && aAfter == 0 && (aIn.Count >= few - 1 || slowStart()),
                      "a menu row's highlight fades in (" + ends(aIn) + ") and moves to the next row (that one " + ends(bIn) + ", the first back to " + aAfter + "); " + clock());
                settle(300);

                // 5b. stage 3: the message list glides (to the bottom, stopped by the user, to a message, after a new one)
                var panelG = (ScrollableControl)Field(f, "_messagePanel");
                var chatG = Field(f, "_selectedChat");
                var msgsG = (IList)Field(f, "_currentChatMessages");
                for (int i = 0; i < 40; i++)   // a chat long enough to glide in
                {
                    var m = new TL.Message { id = 30000 + i, date = DateTime.UtcNow.AddMinutes(-80 + i), peer_id = new TL.PeerUser { user_id = AdaId },
                                             message = "Glide check " + i + ": a few words so the bubble has some height", flags = i % 3 == 0 ? TL.Message.Flags.out_ : 0 };
                    msgsG.Add(m);
                    Invoke(f, "AddMessageBubble", chatG, m, null, null);
                }
                settle(500);
                Func<double> posG = () => -panelG.AutoScrollPosition.Y;
                Func<int> maxG = () => (int)Invoke(f, "MaxMessageScroll");
                // A glide frame repaints the list, so the queue stays busy and one DoEvents can run a whole glide: the
                // positions are recorded from the scroller's own event (every frame raises it). `then` runs (posted, so
                // before the next frame) after `at` frames: the user taking over in the middle.
                Func<Action, int, int, Action, List<double>> recordGlide = (begin, ms, at, then) =>
                {
                    var seen = new List<double> { posG() };
                    int frames = 0;
                    Action<ScrollableControl> h = s =>
                    {
                        if (!ReferenceEquals(s, panelG)) return;
                        double v = posG();
                        if (!seen[seen.Count - 1].Equals(v)) seen.Add(v);
                        if (then != null && ++frames == at) f.BeginInvoke(then);
                    };
                    UI.Controls.TouchScroller.Scrolled += h;
                    try { begin(); watch(ms, posG); }
                    finally { UI.Controls.TouchScroller.Scrolled -= h; }
                    return seen;
                };
                panelG.AutoScrollPosition = new Point(0, 0);
                settle(200);
                Anim.ResetStats();
                var gy = recordGlide(() => Invoke(f, "GlideMessagesToBottom", Anim.GlideMs, null), Anim.GlideMs + 300, 0, null);
                check(gy[gy.Count - 1] == maxG() && oneWay(gy, 1) && (gy.Count >= few || slowStart()) && Anim.Active == 0,
                      "the list glides to the bottom: " + gy.Count + " positions, " + gy[0] + " to " + gy[gy.Count - 1] + " px (the bottom: " + maxG() + "); " + clock());
                // the user moves the list in the middle of a glide (the wheel, the scrollbar): it stops there
                panelG.AutoScrollPosition = new Point(0, 0);
                settle(200);
                int grabbed = -1;
                recordGlide(() => Invoke(f, "GlideMessagesToBottom", Anim.GlideMs, null), 450, 4,
                            () => { grabbed = Math.Max(0, (int)posG() - 40); panelG.AutoScrollPosition = new Point(0, grabbed); });
                bool stoppedByMove = grabbed >= 0 && (int)posG() == grabbed && Anim.Active == 0;
                // a finger lands (no move yet): it stops where it is
                panelG.AutoScrollPosition = new Point(0, 0);
                settle(200);
                int caughtAt = -1;
                var raiseInput = typeof(UI.Controls.TouchScroller).GetMethod("RaiseUserInput", BindingFlags.NonPublic | BindingFlags.Static);
                recordGlide(() => Invoke(f, "GlideMessagesToBottom", Anim.GlideMs, null), 450, 4,
                            () => { raiseInput.Invoke(null, new object[] { panelG }); caughtAt = (int)posG(); });
                bool stoppedByFinger = caughtAt >= 0 && (int)posG() == caughtAt && caughtAt < maxG() && Anim.Active == 0;
                check(stoppedByMove && stoppedByFinger, "a glide stops where the user takes over (moved: " + stoppedByMove + " at " + grabbed + ", a finger landing: "
                      + stoppedByFinger + " at " + caughtAt + ", the bottom " + maxG() + ")");
                // a message far above glides to the middle of the view and flashes when it arrives
                Invoke(f, "ScrollMessagesToBottom");
                settle(300);
                var bubblesG = panelG.Controls.OfType<UI.Controls.MessageBubbleControl>().Where(b => b.MessageId > 0 && b.Visible).ToList();
                var targetG = bubblesG[bubblesG.Count / 3];
                bool aboveView = targetG.Bottom < 0;
                Anim.ResetStats();
                bool found = false, flashEarly = false;
                var ty = recordGlide(() => { found = (bool)Invoke(f, "ScrollToAndFlash", targetG.MessageId); flashEarly = (int)Field(targetG, "_flashTicks") > 0; },
                                     Anim.GlideMs + 300, 0, null);
                int mid = targetG.Top + targetG.Height / 2, viewMid = panelG.ClientSize.Height / 2;
                bool centred = Math.Abs(mid - viewMid) <= Dpi.Px(4, f.WindowDpiValue);
                bool flashed = (int)Field(targetG, "_flashTicks") > 0 || Field(targetG, "_flashTimer") != null;
                check(found && aboveView && centred && flashed && !flashEarly && oneWay(ty, -1) && (ty.Count >= few || slowStart()),
                      "a message far above glides to the middle of the view and flashes when it arrives (" + ty.Count + " positions; its middle " + mid
                      + " px, the view's " + viewMid + " px; flashed at the end: " + (flashed && !flashEarly) + "); " + clock());
                // a new message at the bottom slides in
                Invoke(f, "ScrollMessagesToBottom");
                settle(300);
                var nmG = new TL.Message { id = 30100, date = DateTime.UtcNow, peer_id = new TL.PeerUser { user_id = AdaId }, message = "A new message: it slides in from the bottom" };
                msgsG.Add(nmG);
                Invoke(f, "AddMessageBubble", chatG, nmG, null, null);
                double beforeNew = posG();
                Anim.ResetStats();
                var ny = recordGlide(() => Invoke(f, "GlideMessagesToBottom", Anim.NewMessageMs, null), Anim.NewMessageMs + 300, 0, null);
                check(ny[ny.Count - 1] == maxG() && ny[ny.Count - 1] > beforeNew && oneWay(ny, 1) && (ny.Count >= few - 1 || slowStart()),
                      "a new message at the bottom slides in: " + ny.Count + " positions, " + beforeNew + " to " + ny[ny.Count - 1] + " px; " + clock());
                // "Scrolling glides" off: the same jump is instant
                AppSettings.Instance.AnimateScroll = false;
                panelG.AutoScrollPosition = new Point(0, 0);
                Invoke(f, "GlideMessagesToBottom", Anim.GlideMs, null);
                bool scrollInstant = (int)posG() == maxG() && Anim.Active == 0;
                AppSettings.Instance.AnimateScroll = true;
                check(scrollInstant, "\"Scrolling glides\" off: the jump to the bottom is instant");
                settle(300);

                // 5c. stage 3: in one column the page that leaves slides away (back to the list: right; to the chat: left)
                var wideBounds = f.Bounds;
                f.Width = Dpi.Px(700, f.WindowDpiValue);   // under 800 logical px: one column
                settle(600);
                bool oneCol = (bool)Field(f, "_oneColumn");
                Func<bool> chatShown = () => (bool)f.GetType().GetProperty("ChatColumnShown", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(f, null);
                Func<bool> pictureGone = () => Field(f, "_columnSlide") == null && !((Control)Field(f, "_split")).Parent.Controls.Cast<Control>().Any(c => c.GetType().Name == "ColumnPicture");
                Func<bool, List<double>> slide = toList =>
                {
                    var ds = new List<double>();
                    int splitW = ((Control)Field(f, "_split")).Width;
                    bool shot = false;
                    MainForm.ColumnSlideObserver = d =>
                    {
                        if (ds.Count == 0 || !ds[ds.Count - 1].Equals((double)d)) ds.Add(d);
                        if (!shot && d >= splitW / 3) { shot = true; grab(f.Bounds, toList ? "anim-column-back-midway" : "anim-column-open-midway"); }
                    };
                    try { Invoke(f, "ShowColumn", toList); watch(Anim.ColumnMs + 400, () => ds.Count); }
                    finally { MainForm.ColumnSlideObserver = null; }
                    return ds;
                };
                Anim.ResetStats();
                var back = slide(true);
                int splitWidth = ((Control)Field(f, "_split")).Width;
                bool backOk = oneCol && !chatShown() && pictureGone() && oneWay(back, 1) && back[back.Count - 1] == splitWidth && (back.Count >= few || slowStart());
                check(backOk, "one column, back to the list: the chat slides away to the right (" + back.Count + " steps, to " + (back.Count > 0 ? back[back.Count - 1] : 0)
                      + " of " + splitWidth + " px; the list shown: " + !chatShown() + ", the picture removed: " + pictureGone() + "); " + clock());
                settle(300);
                Anim.ResetStats();
                var toChat = slide(false);
                bool openOk = chatShown() && pictureGone() && oneWay(toChat, 1) && toChat[toChat.Count - 1] == splitWidth && (toChat.Count >= few || slowStart());
                check(openOk, "one column, to the chat: the list slides away to the left (" + toChat.Count + " steps; the chat shown: " + chatShown()
                      + ", the picture removed: " + pictureGone() + "); " + clock());
                settle(300);
                AppSettings.Instance.AnimateColumns = false;
                Invoke(f, "ShowColumn", true);
                bool colInstant = !chatShown() && pictureGone() && Anim.Active == 0;
                Invoke(f, "ShowColumn", false);
                colInstant = colInstant && chatShown() && pictureGone();
                AppSettings.Instance.AnimateColumns = true;
                check(colInstant, "\"Pages slide in a narrow window\" off: the list and the chat switch at once, no picture");
                // the real back arrow (it closes the chat before the switch: review found it never slid)
                Anim.ResetStats();
                var viaBack = new List<double>();
                MainForm.ColumnSlideObserver = d => { if (viaBack.Count == 0 || !viaBack[viaBack.Count - 1].Equals((double)d)) viaBack.Add(d); };
                try { Invoke(f, "OnBackClick"); watch(Anim.ColumnMs + 400, () => viaBack.Count); }
                finally { MainForm.ColumnSlideObserver = null; }
                bool backArrowOk = Field(f, "_selectedChat") == null && pictureGone() && viaBack.Count >= Math.Min(few, 3) && oneWay(viaBack, 1);
                check(backArrowOk, "one column, the back arrow: the chat closes and slides away to the right (" + viaBack.Count + " steps; the picture removed: " + pictureGone() + "); " + clock());
                OpenDemoChat(f);   // the steps after this need the chat open again
                Invoke(f, "ShowColumn", false);
                settle(400);
                f.Bounds = wideBounds;
                settle(600);

                // 6. everything off: nothing is ever put on the clock
                AppSettings.Instance.Animations = false;
                Anim.ResetStats();
                Invoke(f, "ShowDrawer");
                var d4 = (Control)Field(f, "_drawer");
                bool drawerThere = d4.Left == 0;
                Invoke(f, "CloseDrawer", true);
                Invoke(f, "OpenEmojiPicker");
                var picker2 = Application.OpenForms.OfType<EmojiPicker>().FirstOrDefault();
                bool pickerSolid = picker2 != null && picker2.Opacity >= 0.999;
                if (picker2 != null) picker2.Close();
                Invoke(row, "OnMouseEnter", EventArgs.Empty);
                bool hoverAtOnce = hov.T == 1;
                Invoke(row, "OnMouseLeave", EventArgs.Empty);
                int ticks; float tot, worstGap;
                Anim.ReadStats(out ticks, out tot, out worstGap);
                AppSettings.Instance.Animations = true;
                check(drawerThere && d4.IsDisposed && pickerSolid && hoverAtOnce && hov.T == 0 && ticks == 0 && Anim.Active == 0,
                      "\"Animations\" off: the drawer, a popup and a hover are all instant, and the clock never starts (" + ticks + " ticks)");
                settle(300);
                try { System.Threading.Tasks.Task.WaitAll(grabs.ToArray(), 10000); } catch { }   // the captures still being saved
                rigDone = true;
            }
            catch (Exception ex) { Fail("live-anim", ex); }
            finally
            {
                if (st != null) { try { st.Close(); } catch { } }
                if (f != null) { try { f.Close(); } catch { } try { f.Dispose(); } catch { } }
            }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            return _failures == 0 ? 0 : 1;
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
                // 2026-10-16: the header ⋮ menu of a private chat offers a voice and a video call
                using (var headerMenu = new ContextMenuStrip())
                {
                    var surface = Enum.Parse(typeof(MainForm).GetNestedType("ChatMenuSurface", BindingFlags.NonPublic), "ChatHeader");
                    var chat = (ChatEntry)Field(f, "_selectedChat");
                    var info = chat.PeerInfo;
                    chat.PeerInfo = new TL.User { id = AdaId, first_name = "Ada", last_name = "Lovelace" };   // a person, as a real chat has
                    try { Invoke(f, "BuildChatActionMenu", headerMenu, chat, surface); }
                    finally { chat.PeerInfo = info; }
                    var texts = headerMenu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList();
                    bool callsOk = texts.Contains("Call") && texts.Contains("Video call");
                    _log.AppendLine("  " + (callsOk ? "PASS  " : "FAIL  ") + "header menu offers Call and Video call (" + string.Join(" | ", texts) + ")");
                    if (!callsOk) _failures++;
                }
                // R6 (accessibility): what a screen reader reads for the header's glyph buttons and the composer, and the chat
                // rows / bubbles are not Tab stops
                {
                    var names = new List<string>();
                    foreach (var fld in new[] { "_hamburger", "_chatMenuBtn", "_chatSearchBtn", "_dockBtn", "_callBtn", "_sendButton", "_emojiButton", "_attachButton", "_micButton" })
                    {
                        var ctl = Field(f, fld) as Control;
                        names.Add(fld + "=" + (ctl == null ? "(none)" : ctl.AccessibilityObject.Name ?? ""));
                    }
                    var input = Field(f, "_messageInput");
                    var inner = input == null ? null : input.GetType().GetField("Rich").GetValue(input) as Control;   // InputBox.Rich: the EmojiComposer
                    string composerName = inner != null ? inner.AccessibilityObject.Name : null;
                    var rows = new List<Control>();
                    Action<Control> walk = null;
                    walk = c => { foreach (Control k in c.Controls) { if (k is UI.Controls.ChatListItemControl || k is UI.Controls.MessageBubbleControl) rows.Add(k); walk(k); } };
                    walk(f);
                    bool named = names.TrueForAll(n => !n.EndsWith("=") && !n.EndsWith("(none)"));
                    bool noStops = rows.Count > 0 && rows.TrueForAll(r => !r.TabStop);
                    _log.AppendLine("  " + (named ? "PASS  " : "FAIL  ") + "R6: the header and composer buttons have names (" + string.Join(", ", names) + ")");
                    _log.AppendLine("  info  R6: the composer reads as \"" + (composerName ?? "(not found)") + "\"");
                    _log.AppendLine("  " + (noStops ? "PASS  " : "FAIL  ") + "R6: chat rows and bubbles are not Tab stops (" + rows.Count + " checked)");
                    if (!named) _failures++;
                    if (!noStops) _failures++;
                }
                BidiCheck();
                ListKeysCheck(f);
                // 2026-10-16: a group with a voice chat on - the header's call button becomes the voice chat button (shot) and
                // the ⋮ menu offers to join; an admin's group without one offers to start one
                {
                    var surface = Enum.Parse(typeof(MainForm).GetNestedType("ChatMenuSurface", BindingFlags.NonPublic), "ChatHeader");
                    var chat = (ChatEntry)Field(f, "_selectedChat");
                    var info = chat.PeerInfo; var peer = chat.Peer; bool phoneShown = callBtn.Visible;
                    // the person's chat first: the phone, never the voice chat icon (owner 2026-10-16), even with no PeerInfo
                    bool personPhone = !(bool)Invoke(f, "IsGroupOrChannel", chat);
                    _log.AppendLine("  " + (personPhone ? "PASS  " : "FAIL  ") + "a private chat's header button is the phone (PeerInfo " + (chat.PeerInfo == null ? "none" : chat.PeerInfo.GetType().Name) + ")");
                    if (!personPhone) _failures++;
                    Capture(f, "keys-call-header");
                    var club = new TL.Channel { id = 4242, title = "Analytical Engine club", flags = TL.Channel.Flags.megagroup | TL.Channel.Flags.call_active };
                    chat.PeerInfo = club;
                    chat.Peer = new TL.InputPeerChannel(4242, 1);
                    try
                    {
                        Func<string> menuTexts = () =>
                        {
                            using (var m = new ContextMenuStrip())
                            {
                                Invoke(f, "BuildChatActionMenu", m, chat, surface);
                                return string.Join(" | ", m.Items.Cast<ToolStripItem>().Select(i => i.Text));
                            }
                        };
                        bool voice = !(bool)Invoke(f, "CanCall", chat) && (bool)Invoke(f, "CanVoiceChat", chat) && (bool)Invoke(f, "IsGroupOrChannel", chat);
                        string joinMenu = menuTexts();
                        bool joinOk = voice && joinMenu.Contains("Join voice chat") && !joinMenu.Contains("Video call");
                        _log.AppendLine("  " + (joinOk ? "PASS  " : "FAIL  ") + "a group with a voice chat on: the voice chat button, and the menu offers to join (" + joinMenu + ")");
                        if (!joinOk) _failures++;
                        callBtn.Visible = true;
                        Invoke(f, "PlaceCallButton");
                        callBtn.Invalidate();
                        LivePump(300);
                        Capture(f, "keys-voicechat-header");
                        club.flags = TL.Channel.Flags.megagroup | TL.Channel.Flags.creator;   // no voice chat; we may start one
                        string startMenu = menuTexts();
                        club.flags = TL.Channel.Flags.megagroup;                              // neither: no button, no item
                        string noneMenu = menuTexts();
                        bool none = !(bool)Invoke(f, "CanVoiceChat", chat);
                        bool startOk = startMenu.Contains("Start voice chat") && none && !noneMenu.Contains("voice chat");
                        _log.AppendLine("  " + (startOk ? "PASS  " : "FAIL  ") + "an admin may start one; a member of a quiet group sees nothing (" + startMenu + ")");
                        if (!startOk) _failures++;
                    }
                    finally { chat.PeerInfo = info; chat.Peer = peer; callBtn.Visible = phoneShown; callBtn.Invalidate(); }
                }
                // owner 2026-10-16: bubbles follow the first strong letter, as the composer (fonts still by "has Persian")
                {
                    var texts = new[] { "Hello سلام - mixed, Latin first", "سلام hello - Persian first", "123 سلام - digits first" };
                    var want = new[] { RightToLeft.No, RightToLeft.Yes, RightToLeft.Yes };
                    var got = new List<string>();
                    bool dirOk = true;
                    for (int i = 0; i < texts.Length; i++)
                    {
                        var m = new TL.Message { id = 9101 + i, date = DateTime.UtcNow, peer_id = new TL.PeerUser { user_id = 900001 }, message = texts[i],
                                                 flags = i == 1 ? TL.Message.Flags.out_ : 0 };
                        Invoke(f, "AddMessageBubble", Field(f, "_selectedChat"), m, null, null);
                    }
                    var bubbles = new List<Control>();
                    Action<Control> find = null;
                    find = c => { foreach (Control k in c.Controls) { if (k is UI.Controls.MessageBubbleControl) bubbles.Add(k); find(k); } };
                    find(f);
                    for (int i = 0; i < texts.Length; i++)
                    {
                        var bubble = bubbles.FirstOrDefault(c => (Field(c, "_text") as string) == texts[i]);
                        var rtl = bubble != null ? bubble.RightToLeft : RightToLeft.Inherit;
                        got.Add(rtl.ToString());
                        if (rtl != want[i]) dirOk = false;
                    }
                    Invoke(f, "ScrollMessagesToBottom");
                    LivePump(500);
                    Capture(f, "keys-bubble-direction");
                    _log.AppendLine("  " + (dirOk ? "PASS  " : "FAIL  ") + "bubbles: direction by the first strong letter (Latin-first LTR, Persian-first and digits-first RTL: " + string.Join(", ", got) + ")");
                    if (!dirOk) _failures++;
                }
                // owner 2026-11-06: a GROUP of files (PDFs, a ZIP...) was drawn as blank picture tiles. Through the real path
                // (AddMessageBubble -> HandleAlbumItem) it is one bubble with a file row per item now; grouped audio keeps its
                // audio rows; a tap on a file row with no connection does nothing harmful; a file found on disk reads as cached.
                {
                    Func<int, long, long, string, string, long, TL.DocumentAttribute, TL.Message> grouped = (id, group, docId, name, mime, size, extra) => new TL.Message
                    {
                        id = id, date = DateTime.UtcNow, peer_id = new TL.PeerUser { user_id = 900001 }, message = "", grouped_id = group,
                        media = new TL.MessageMediaDocument
                        {
                            document = new TL.Document
                            {
                                id = docId, size = size, mime_type = mime,
                                attributes = extra != null ? new[] { new TL.DocumentAttributeFilename { file_name = name }, extra }
                                                           : new TL.DocumentAttribute[] { new TL.DocumentAttributeFilename { file_name = name } },
                            },
                        },
                    };
                    var files = new[]
                    {
                        grouped(9301, 55501, 70001, "Analytical Engine - notes.pdf", "application/pdf", 2411724, null),
                        grouped(9302, 55501, 70002, "Engine drawings.zip", "application/zip", 18874368, null),
                        grouped(9303, 55501, 70003, "Budget 1843.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 48211, null),
                        grouped(9304, 55501, 70004, "Letter to Babbage.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", 21877, null),
                    };
                    files[3].message = "The files for Monday";   // the caption rides on one item of a group
                    var songs = new[]
                    {
                        grouped(9311, 55502, 70011, "01 Overture.mp3", "audio/mpeg", 5242880, new TL.DocumentAttributeAudio { duration = 212, title = "Overture", performer = "The Difference Engines" }),
                        grouped(9312, 55502, 70012, "02 Punch Cards.mp3", "audio/mpeg", 4194304, new TL.DocumentAttributeAudio { duration = 187, title = "Punch Cards", performer = "The Difference Engines" }),
                    };
                    var chatMsgs = (IList)Field(f, "_currentChatMessages");
                    foreach (var m in files.Concat(songs)) { chatMsgs.Add(m); Invoke(f, "AddMessageBubble", Field(f, "_selectedChat"), m, null, null); }
                    Invoke(f, "ScrollMessagesToBottom");
                    LivePump(600);
                    var panelNow = (Control)Field(f, "_messagePanel");
                    var bubblesNow = panelNow.Controls.OfType<UI.Controls.MessageBubbleControl>().ToList();
                    var fileAlbum = bubblesNow.FirstOrDefault(b => b.IsAlbum && b.ContainsMessageId(9302));
                    var songAlbum = bubblesNow.FirstOrDefault(b => b.IsAlbum && b.ContainsMessageId(9312));
                    Func<UI.Controls.MessageBubbleControl, string, List<bool>> flags = (b, field) =>
                        ((IEnumerable)Field(b, "_album")).Cast<object>().Select(t => (bool)t.GetType().GetField(field).GetValue(t)).ToList();
                    bool oneBubbleEach = fileAlbum != null && songAlbum != null && !ReferenceEquals(fileAlbum, songAlbum)
                                         && bubblesNow.Count(b => b.IsAlbum && (b.ContainsMessageId(9301) || b.ContainsMessageId(9304))) == 1;
                    bool fileRows = fileAlbum != null && (bool)Field(fileAlbum, "_albumRows") && flags(fileAlbum, "IsFile").Count == 4 && flags(fileAlbum, "IsFile").All(x => x)
                                    && flags(fileAlbum, "IsAudio").All(x => !x);
                    bool audioRows = songAlbum != null && (bool)Field(songAlbum, "_albumRows") && flags(songAlbum, "IsAudio").Count == 2 && flags(songAlbum, "IsAudio").All(x => x)
                                     && flags(songAlbum, "IsFile").All(x => !x);
                    // a tap in the middle of the second file row: no connection here, so nothing starts - and nothing throws
                    bool tapOk = false; string tapNote = "";
                    if (fileAlbum != null)
                    {
                        var rects = ((IEnumerable)Field(fileAlbum, "_albumTileRects")).Cast<KeyValuePair<int, Rectangle>>().ToList();
                        var row = rects.FirstOrDefault(kv => kv.Key == 9302).Value;
                        try
                        {
                            Invoke(fileAlbum, "OnMouseClick", new MouseEventArgs(MouseButtons.Left, 1, row.X + row.Width / 2, row.Y + row.Height / 2, 0));
                            tapOk = rects.Count == 4 && row.Width > 0;
                        }
                        catch (Exception ex) { tapNote = ex.GetType().Name + ": " + ex.Message; }
                    }
                    // the first file is on disk (the same cache path the single file card uses): its row reads as cached
                    bool cachedOk = false;
                    if (fileAlbum != null)
                    {
                        string onDisk = TelegArm.Core.MediaCache.MediaPath(TelegArm.Core.MediaCache.CacheFileName("document", 70001, "Analytical Engine - notes.pdf"));
                        File.WriteAllBytes(onDisk, new byte[2411724]);
                        Invoke(f, "ConfigureAlbumFile", fileAlbum, files[0]);
                        cachedOk = flags(fileAlbum, "Cached")[0] && !flags(fileAlbum, "Cached")[1];
                        try { File.Delete(onDisk); } catch { }
                    }
                    LivePump(300);
                    Capture(f, "keys-album-files");
                    if (fileAlbum != null)   // the whole bubble, whatever part of it the window shows
                        using (var whole = new Bitmap(fileAlbum.Width, fileAlbum.Height))
                        {
                            fileAlbum.DrawToBitmap(whole, new Rectangle(Point.Empty, fileAlbum.Size));
                            whole.Save(Path.Combine(_dir, "keys-album-files-bubble.png"), ImageFormat.Png);
                        }
                    bool albumOk = oneBubbleEach && fileRows && audioRows && tapOk && cachedOk;
                    _log.AppendLine("  " + (albumOk ? "PASS  " : "FAIL  ") + "a group of files is one bubble of file rows (one bubble each: " + oneBubbleEach + ", file rows: " + fileRows
                        + ", audio rows kept: " + audioRows + ", tap: " + (tapOk ? "ok" : "FAILED " + tapNote) + ", cached file seen: " + cachedOk + ")");
                    if (!albumOk) _failures++;
                }
                // owner 2026-11-13 ("fix the group right click"): the menu on an item of a group acts on THAT item, on the rest
                // of the bubble (the caption) on the whole group; and a deleted item leaves the rest of its group on the screen
                try
                {
                    var panel2 = (Control)Field(f, "_messagePanel");
                    Func<int, UI.Controls.MessageBubbleControl> albumOf = id =>
                        panel2.Controls.OfType<UI.Controls.MessageBubbleControl>().FirstOrDefault(b => b.IsAlbum && b.ContainsMessageId(id));
                    var group = albumOf(9302);
                    var songs2 = albumOf(9312);
                    var rowRects = ((IEnumerable)Field(group, "_albumTileRects")).Cast<KeyValuePair<int, Rectangle>>().ToList();
                    var third = rowRects.First(kv => kv.Key == 9303).Value;
                    var onRow = new Point(third.X + third.Width / 2, third.Y + third.Height / 2);
                    int below = rowRects.Max(kv => kv.Value.Bottom);
                    var onCaption = new Point(third.X + third.Width / 2, below + Math.Max(2, (group.Height - below) / 2));
                    bool hit = group.AlbumMessageIdAt(onRow) == 9303 && group.AlbumMessageIdAt(onCaption) == 0;
                    Func<Point, List<string>> menuAt = pt =>
                    {
                        Invoke(f, "OnBubbleContextMenu", group, group.PointToScreen(pt));
                        LivePump(300);
                        var m = Field(f, "_messageMenu") as ToolStripDropDown;
                        var labels = m == null ? new List<string>() : m.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToList();
                        if (m != null) m.Close();
                        LivePump(200);
                        return labels;
                    };
                    var itemMenu = menuAt(onRow);
                    var groupMenu = menuAt(onCaption);
                    bool itemOk = itemMenu.Any(t => t.EndsWith("Delete")) && itemMenu.Any(t => t.EndsWith("Save")) && itemMenu.Any(t => t.EndsWith("Forward"))
                                  && itemMenu.Any(t => t.EndsWith("Reveal in folder")) && !itemMenu.Any(t => t.EndsWith("(4)"));
                    bool groupOk = groupMenu.Any(t => t.EndsWith("Delete (4)")) && groupMenu.Any(t => t.EndsWith("Save (4)"))
                                   && groupMenu.Any(t => t.EndsWith("Forward (4)")) && groupMenu.Any(t => t.EndsWith("Copy text"));
                    var liveMsgs = (IList)Field(f, "_currentChatMessages");
                    // review 2026-11-13: an EDIT of one item of a group (ours, or from another device) changes the caption in
                    // place. Rebuilding the bubble from that one message left only it on the screen.
                    var captionMsg = liveMsgs.Cast<TL.Message>().First(m => m.ID == 9304);
                    captionMsg.message = "The files for Tuesday";
                    Invoke(f, "RebuildBubble", 9304, captionMsg);
                    bool captionEdited = panel2.Controls.Contains(group) && group.AlbumCount == 4 && (Field(group, "_text") as string) == "The files for Tuesday";
                    Invoke(f, "RebuildBubble", 9301, liveMsgs.Cast<TL.Message>().First(m => m.ID == 9301));
                    bool firstEdited = panel2.Controls.Contains(group) && !group.IsDisposed && group.AlbumCount == 4 && ReferenceEquals(albumOf(9303), group);
                    bool editOk = captionEdited && firstEdited;
                    _log.AppendLine("  " + (editOk ? "PASS  " : "FAIL  ") + "an edit of one item of a group changes the caption in place and keeps the group (caption item: "
                        + captionEdited + ", first item: " + firstEdited + ")");
                    if (!editOk) _failures++;
                    Func<int, int> remove = id => (int)Invoke(f, "RemoveMessagesFromView", new HashSet<int> { id });
                    int r1 = remove(9302);
                    bool one = r1 == 1 && group.AlbumCount == 3 && !group.ContainsMessageId(9302) && panel2.Controls.Contains(group) && group.MessageId == 9301;
                    int r2 = remove(9301);
                    bool rekeyed = r2 == 1 && group.AlbumCount == 2 && group.MessageId == 9303;
                    LivePump(300);
                    using (var whole = new Bitmap(group.Width, group.Height))
                    {
                        group.DrawToBitmap(whole, new Rectangle(Point.Empty, group.Size));
                        whole.Save(Path.Combine(_dir, "keys-album-files-two-left.png"), ImageFormat.Png);
                    }
                    bool hadCaption = !string.IsNullOrEmpty(Field(group, "_text") as string) && group.AlbumCaptionMessageId == 9304;
                    int r3 = remove(9304);
                    bool captionGone = hadCaption && r3 == 1 && group.AlbumCount == 1 && group.AlbumCaptionMessageId == 0
                                       && string.IsNullOrEmpty(Field(group, "_text") as string);
                    int r4 = remove(9303);
                    bool gone = r4 == 1 && !panel2.Controls.Contains(group) && group.IsDisposed;
                    bool othersKept = songs2 != null && songs2.AlbumCount == 2 && panel2.Controls.Contains(songs2)
                                      && !liveMsgs.Cast<TL.Message>().Any(m => m.ID >= 9301 && m.ID <= 9304)
                                      && liveMsgs.Cast<TL.Message>().Count(m => m.ID == 9311 || m.ID == 9312) == 2;
                    bool groupMenuOk = hit && itemOk && groupOk && one && rekeyed && captionGone && gone && othersKept;
                    _log.AppendLine("  " + (groupMenuOk ? "PASS  " : "FAIL  ") + "a group's menu acts on the item under the pointer, and a deleted item leaves the rest (hit test: " + hit
                        + ", item menu: " + itemOk + ", group menu: " + groupOk + ", one row gone: " + one + ", first gone, next takes over: " + rekeyed
                        + ", caption goes with its item: " + captionGone + ", last gone, bubble gone: " + gone + ", other group kept: " + othersKept + ")");
                    if (!groupMenuOk)
                    {
                        _failures++;
                        _log.AppendLine("    item menu: " + string.Join(" | ", itemMenu));
                        _log.AppendLine("    group menu: " + string.Join(" | ", groupMenu));
                    }
                }
                catch (Exception ex)
                {
                    _failures++;
                    _log.AppendLine("  FAIL  a group's menu acts on the item under the pointer: " + ex.GetType().Name + ": " + ex.Message);
                }
                // RT trip 2026-10-16 ("pause can't be resumed, can't stop or cancel"): the controls act AT ONCE, never waiting for
                // a transfer in flight (none here: no network) - pause, resume, pause, cancel; and a pause before a deferred start
                {
                    var tmp = Path.Combine(_dir, "dl-state.bin");
                    var h = new DownloadHandle(4711, tmp);
                    var seen = new List<string>();
                    h.Changed += x => seen.Add(x.State.ToString());
                    h.Pause(); string a = h.State.ToString();
                    h.Resume(); string b = h.State.ToString();
                    h.Pause(); string c = h.State.ToString();
                    h.Cancel(); string d = h.State.ToString();
                    h.Resume(); string e2 = h.State.ToString();   // finished: stays cancelled
                    bool flow = a == "Paused" && b == "Downloading" && c == "Paused" && d == "Cancelled" && e2 == "Cancelled" && h.Completion.IsCompleted;
                    var h2 = new DownloadHandle(4712, tmp);
                    h2.Pause();
                    Invoke(h2, "Run", null, null, null, null);   // the deferred start arrives while paused: it must not start
                    bool deferred = h2.State == DownloadHandle.DState.Paused && !h2.Completion.IsCompleted;
                    h2.Cancel();
                    deferred = deferred && h2.State == DownloadHandle.DState.Cancelled;
                    _log.AppendLine("  " + (flow && deferred ? "PASS  " : "FAIL  ") + "downloads: pause / resume / cancel act at once (" + a + " " + b + " " + c + " " + d + " " + e2
                        + "; paused before its start: " + (deferred ? "stays paused, then cancels" : "WRONG") + "; events " + string.Join(",", seen) + ")");
                    if (!(flow && deferred)) _failures++;
                }
                // owner 2026-10-16: the gallery's scale round trip, on the REAL main window - the interface scale applied live and
                // back (as Settings does): the chat rows and the bubbles must come back to the same pixels. The window is first
                // sized so the bigger scale is not clamped by the work area.
                {
                    int old = Dpi.UserScalePercent;
                    var scaleArea = Screen.FromHandle(f.Handle).WorkingArea;
                    int cap = Dpi.FitCap(scaleArea, f.MonitorDpiValue);
                    if (cap < 125) _log.AppendLine("  info  scale round trip skipped: this screen is capped at " + cap + "%");
                    else
                    {
                        int target = Math.Min(150, cap);
                        var before = f.Bounds;
                        f.Size = new Size(scaleArea.Width * 55 / 100, scaleArea.Height * 60 / 100);
                        Invoke(f, "ScrollMessagesToBottom");
                        LivePump(600);
                        var kit = new List<Control>();
                        Action<Control> collect = null;
                        collect = c => { foreach (Control k in c.Controls) { if ((k is UI.Controls.ChatListItemControl || k is UI.Controls.MessageBubbleControl) && k.Visible) kit.Add(k); collect(k); } };
                        collect(f);
                        var home = kit.Select(k => k.Bounds).ToArray();
                        var size = f.Size; int dpi = f.WindowDpiValue;
                        AppForm.ApplyInterfaceScale(target);
                        LivePump(900);
                        int scaled = f.WindowDpiValue;
                        AppForm.ApplyInterfaceScale(old);
                        LivePump(900);
                        Invoke(f, "ScrollMessagesToBottom");
                        LivePump(400);
                        var moved = new List<string>();
                        for (int i = 0; i < kit.Count; i++)
                            if (!kit[i].IsDisposed && kit[i].Bounds != home[i]) moved.Add(kit[i].GetType().Name + " " + home[i] + " -> " + kit[i].Bounds);
                        bool same = moved.Count == 0 && f.Size == size && f.WindowDpiValue == dpi && scaled != dpi;
                        _log.AppendLine("  " + (same ? "PASS  " : "FAIL  ") + "main window: interface scale " + target + "% and back - " + kit.Count + " chat rows / bubbles on the same pixels"
                            + " (dpi " + dpi + " -> " + scaled + " -> " + f.WindowDpiValue + ", size " + size + " -> " + f.Size + ")");
                        foreach (var mv in moved.Take(5)) _log.AppendLine("          moved: " + mv);
                        if (!same) _failures++;
                        f.Bounds = before;   // back to two columns for the steps below
                        LivePump(400);
                    }
                }
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
                // owner 2026-10-16: the grid opens ON its message (as from right-click / hold -> React -> More…), wherever the
                // pointer is - the pointer is parked far away to prove it
                var msgPanel = (Control)Field(f, "_messagePanel");
                if (Field(f, "_selectedChat") == null) { OpenDemoChat(f); LivePump(600); }   // the steps above leave no chat open
                var reactTo = new TL.Message { id = 9201, date = DateTime.UtcNow, peer_id = new TL.PeerUser { user_id = 900001 }, message = "React to this one" };
                ((IList)Field(f, "_currentChatMessages")).Add(reactTo);
                Invoke(f, "AddMessageBubble", Field(f, "_selectedChat"), reactTo, null, null);
                Invoke(f, "ScrollMessagesToBottom");
                LivePump(500);
                var loaded = ((IEnumerable)Field(f, "_currentChatMessages")).Cast<TL.Message>().ToList();
                var lastBubble = msgPanel.Controls.OfType<UI.Controls.MessageBubbleControl>()
                                          .Where(b => b.Visible && loaded.Any(m => m.ID == b.MessageId) && msgPanel.ClientRectangle.IntersectsWith(b.Bounds))
                                          .OrderByDescending(b => b.Top).FirstOrDefault();
                var firstMsg = lastBubble == null ? null : loaded.First(m => m.ID == lastBubble.MessageId);
                _log.AppendLine("  info  reaction grid target: " + loaded.Count + " loaded (" + string.Join(",", loaded.Take(8).Select(m => m.ID)) + "), bubbles "
                    + string.Join(",", msgPanel.Controls.OfType<UI.Controls.MessageBubbleControl>().Take(12).Select(b => b.MessageId + (b.Visible ? "" : "h") + "@" + b.Top)));
                Cursor.Position = f.PointToScreen(new Point(4, 4));
                Invoke(f, "ShowReactionGrid", firstMsg);
                LivePump(800);
                var drop = Field(f, "_reactionGrid") as ToolStripDropDown;
                var bubbleRect = lastBubble != null ? lastBubble.RectangleToScreen(lastBubble.ClientRectangle) : Rectangle.Empty;
                var gridRect = drop != null ? drop.Bounds : Rectangle.Empty;
                bool onMessage = firstMsg != null && drop != null && gridRect.Width > 0
                    && Math.Min(Math.Abs(gridRect.Bottom - bubbleRect.Top), Math.Abs(gridRect.Top - bubbleRect.Bottom)) <= 30
                    && gridRect.Right > bubbleRect.Left && gridRect.Left < bubbleRect.Right;
                _log.AppendLine("  " + (onMessage ? "PASS  " : "FAIL  ") + "the reaction grid opens on its message, not at the pointer (grid " + gridRect + ", bubble " + bubbleRect
                    + (lastBubble != null ? (lastBubble.Outgoing ? ", outgoing" : ", incoming") : "") + ")");
                if (!onMessage) _failures++;
                var shot = Rectangle.Union(gridRect, bubbleRect); shot.Inflate(12, 12);
                shot.Intersect(Screen.FromControl(f).Bounds);
                if (shot.Width > 0 && shot.Height > 0)
                    using (var bmp = new Bitmap(shot.Width, shot.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(shot.Location, Point.Empty, shot.Size);
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

        /// <summary>
        /// 2026-11-15 (polish): mixed Persian and English in one bubble. Each paragraph takes the direction of its first
        /// letter, English words keep their order inside a Persian line and Persian ones inside an English line
        /// (InlineText.ResolveLevels), and a plain message with a Persian and an English line goes through the inline
        /// engine. Read from the laid-out words (reflection); keys-bidi.png shows the bubbles.
        /// </summary>
        private static void BidiCheck()
        {
            var samples = new[]
            {
                "Hello there my friend, how are you?\nسلام دوست من\n😀😀",
                "سلام دوست من\nHello there my friend!",
                "من از Windows RT استفاده می‌کنم 😀",
                "I put TelegArm روی سرفیس today 😀",   // (an emoji: one plain line is drawn by Windows' own bidi, not the inline engine)
                "سلام! 😀",
                "سلام!",                              // plain: the reference for the mark's side in the shot
                "من iPhone, iPad دارم 😀",
            };
            var p = ThemeHelper.Palette;
            var wa = Screen.PrimaryScreen.WorkingArea;
            using (var host = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true, BackColor = Color.White })
            {
                host.Bounds = new Rectangle(wa.Left + 20, wa.Top + 20, Math.Min(640, wa.Width - 40), Math.Min(620, wa.Height - 40));
                var bubbles = new List<UI.Controls.MessageBubbleControl>();
                int y = 8;
                for (int i = 0; i < samples.Length; i++)
                {
                    bool outgoing = i % 2 == 1;
                    var b = new UI.Controls.MessageBubbleControl(samples[i], null, outgoing, DateTime.Now)
                    { AccentColor = p.Accent, IsDark = p.Dark, Location = new Point(8, y), Width = host.ClientSize.Width - 16, Read = outgoing };
                    b.SetEntities(null);   // as CreateBubble does: the bubble picks the inline engine itself
                    host.Controls.Add(b);
                    b.Measure();
                    y += b.Height + 6;
                    bubbles.Add(b);
                }
                host.Show();
                LivePump(600);

                Func<object, string> text = t => (string)Field(t, "Text");
                Func<object, int> x = t => ((Rectangle)Field(t, "Rect")).X;
                Func<int, IList> lines = i => { var rich = Field(bubbles[i], "_rich"); return rich == null ? null : (IList)Field(rich, "_lines"); };
                Func<int, string> dirs = i => { var ls = lines(i); return ls == null ? "(plain)" : string.Join(",", ls.Cast<object>().Select(l => (bool)Field(l, "R") ? "RTL" : "LTR")); };
                Func<int, int, List<object>> toks = (i, li) => ((IList)Field(lines(i)[li], "Toks")).Cast<object>().ToList();
                Func<List<object>, string, object> find = (ts, s) => ts.FirstOrDefault(t => text(t) == s);
                Action<bool, string> check = (ok, what) => { _log.AppendLine("  " + (ok ? "PASS  " : "FAIL  ") + "bidi: " + what); if (!ok) _failures++; };
                string order = "";
                try
                {
                    var rich0 = Field(bubbles[0], "_rich");
                    int w0 = (int)rich0.GetType().GetProperty("Width").GetValue(rich0, null);
                    var fa0 = toks(0, 1);
                    int faRight = fa0.Max(t => x(t) + (int)Field(t, "W"));
                    check(dirs(0) == "LTR,RTL,LTR" && toks(0, 0).Min(x) == 0 && faRight == w0,
                          "an English, a Persian and an emoji line each read their own way, the Persian one on the right (" + dirs(0) + "; right edge " + faRight + " of " + w0 + ")");
                    var l1 = toks(1, 1);
                    order = string.Join(" ", l1.OrderBy(x).Select(text));
                    check(dirs(1) == "RTL,LTR" && x(find(l1, "Hello")) < x(find(l1, "friend!")) && l1.Min(x) == 0,
                          "a plain Persian line then an English one: the English line sits left, in order (" + dirs(1) + "; " + order + ")");
                    var l2 = toks(2, 0);
                    order = string.Join(" ", l2.OrderBy(x).Select(text));
                    check(x(find(l2, "Windows")) < x(find(l2, "RT")) && x(find(l2, "من")) == l2.Max(x),
                          "\"Windows RT\" keeps its order inside a Persian line (" + order + ")");
                    var l3 = toks(3, 0);
                    order = string.Join(" ", l3.OrderBy(x).Select(text));
                    check(x(find(l3, "I")) == l3.Min(x) && x(find(l3, "سرفیس")) < x(find(l3, "روی")) && x(find(l3, "روی")) < x(find(l3, "today")),
                          "two Persian words inside an English line read right to left (" + order + ")");
                    var l4 = toks(4, 0);
                    check(x(find(l4, "😀")) < x(find(l4, "سلام!")), "an emoji after a Persian word sits on its left");
                    // the punctuation at a word's edge: with the paragraph after a Persian word ("سلام!" drawn right to left: the
                    // mark on the left), with the next word inside an English run ("iPhone," drawn left to right: "iPhone, iPad")
                    var l6 = toks(6, 0);
                    var iphone = find(l6, "iPhone,");
                    bool drawOk = (bool)Field(find(l4, "سلام!"), "DrawR") && !(bool)Field(iphone, "DrawR") && x(iphone) < x(find(l6, "iPad"));
                    order = string.Join(" ", l6.OrderBy(x).Select(text));
                    check(drawOk, "a word's edge punctuation sides with its neighbours (\"سلام!\" right to left, \"iPhone, iPad\" left to right: " + order + ")");
                    // the selection follows the screen: the word under the pointer is the word copied
                    var rich2 = Field(bubbles[2], "_rich");
                    var win = find(l2, "Windows"); var man = find(l2, "من");
                    var rw = (Rectangle)Field(win, "Rect"); var rm = (Rectangle)Field(man, "Rect");
                    int iw = (int)Invoke(rich2, "HitChar", new Point(rw.X + 1, rw.Y + 2));
                    int im = (int)Invoke(rich2, "HitChar", new Point(rm.Right - 1, rm.Y + 2));
                    string sw = (string)Invoke(rich2, "GetRangeText", iw, iw + 7), sm = (string)Invoke(rich2, "GetRangeText", im, im + 2);
                    check(sw == "Windows" && sm == "من", "a press at a word's start selects from that word (\"" + sw + "\", \"" + sm + "\")");
                    // the highlight follows the screen too: a selected word is lit where it sits, and a selection from an English
                    // line into a Persian one runs to the end of the first and in from the right edge of the second
                    Func<object, int, int, Bitmap> paintSel = (rich, a, b) =>
                    {
                        var bm = new Bitmap((int)rich.GetType().GetProperty("Width").GetValue(rich, null), (int)rich.GetType().GetProperty("Height").GetValue(rich, null));
                        using (var g = Graphics.FromImage(bm)) { g.Clear(Color.White); Invoke(rich, "PaintSelectionHighlight", g, 0, 0, a, b, Color.Black); }
                        return bm;
                    };
                    Func<Bitmap, int, string> lit = (bm, row) => { int lo = -1, hi = -1; for (int i = 0; i < bm.Width; i++) if (bm.GetPixel(i, row).R < 128) { if (lo < 0) lo = i; hi = i; } return lo + ".." + hi; };
                    using (var bm = paintSel(rich2, iw, iw + 7))
                    {
                        string got = lit(bm, rw.Y + 2), want = rw.X + ".." + (rw.Right - 1);
                        check(got == want, "a selected English word in a Persian line is lit where it sits (" + got + ", the word " + want + ")");
                    }
                    var my = find(toks(0, 0), "my"); var dust = find(fa0, "دوست");
                    var rMy = (Rectangle)Field(my, "Rect"); var rDust = (Rectangle)Field(dust, "Rect");
                    using (var bm = paintSel(rich0, (int)Field(my, "SelStart"), (int)Field(dust, "SelStart") + 4))
                    {
                        string top = lit(bm, rMy.Y + 2), bottom = lit(bm, rDust.Y + 2);
                        check(top == rMy.X + ".." + (w0 - 1) && bottom == rDust.X + ".." + (w0 - 1),
                              "a selection from an English line into a Persian one: to the end of the first, in from the right of the second (" + top + " / " + bottom + ")");
                    }
                }
                catch (Exception ex) { check(false, "the check threw " + ex.GetType().Name + ": " + ex.Message + " (" + order + ")"); }
                var shot = host.Bounds;
                shot.Intersect(Screen.FromControl(host).Bounds);
                if (shot.Width > 0 && shot.Height > 0)
                    using (var bmp = new Bitmap(shot.Width, shot.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(shot.Location, Point.Empty, shot.Size);
                        bmp.Save(Path.Combine(_dir, "keys-bidi.png"), ImageFormat.Png);
                    }
                host.Close();
            }
        }

        /// <summary>
        /// 2026-11-16 (R6): the keyboard in the chat list and in a dialog list. In the search box Down / Up move a ring
        /// through the chat list (the focus stays in the box) and Enter opens that chat; while searching, Enter opens the
        /// first result. A dialog list is reached from its search box with Down, its arrows move the ring, Space picks,
        /// Up on the first row goes back to the box, and a tap on a row picks it WITHOUT taking the focus (the touch
        /// keyboard's case). Keys and the tap are POSTED, so they take WinForms' ordinary path. No account here, so
        /// Enter's chat goes to MainForm.OpenFromKeysHook instead of opening.
        /// </summary>
        private static void ListKeysCheck(MainForm f)
        {
            Action<bool, string> check = (ok, what) => { _log.AppendLine("  " + (ok ? "PASS  " : "FAIL  ") + "keys: " + what); if (!ok) _failures++; };
            var search = Field(f, "_searchBox");
            var box = (Control)search.GetType().GetProperty("Control").GetValue(search, null);
            var opened = new List<ChatEntry>();
            MainForm.OpenFromKeysHook = e => opened.Add(e);
            try
            {
                f.Activate(); LivePump(300);
                box.Focus(); LivePump(200);
                Func<List<UI.Controls.ChatListItemControl>> rows = () => ((IEnumerable)Invoke(f, "ShownRows")).Cast<UI.Controls.ChatListItemControl>().ToList();
                Func<int> ring = () => rows().FindIndex(r => r.KeyCursor);
                PostKey(f, Keys.Down); int k1 = ring();
                PostKey(f, Keys.Down); int k2 = ring();
                PostKey(f, Keys.Up); int k3 = ring();
                check(box.ContainsFocus && k1 >= 0 && k2 == k1 + 1 && k3 == k1,
                      "Down / Up in the search box move a ring through the chat list, the focus stays in the box (rows " + k1 + ", " + k2 + ", " + k3 + ")");
                var list = (Control)Field(f, "_chatListPanel");
                var lr = list.RectangleToScreen(list.ClientRectangle);
                lr.Intersect(Screen.FromControl(f).Bounds);
                if (lr.Width > 0 && lr.Height > 0)
                    using (var bmp = new Bitmap(lr.Width, lr.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(lr.Location, Point.Empty, lr.Size);
                        bmp.Save(Path.Combine(_dir, "keys-chat-ring.png"), ImageFormat.Png);
                    }
                var want = k3 >= 0 ? rows()[k3].Entry : null;
                PostKey(f, Keys.Enter);
                check(opened.Count == 1 && want != null && ReferenceEquals(opened[0], want) && ring() < 0,
                      "Enter opens the chat under the ring (" + (opened.Count > 0 ? opened[0].Title : "none") + ")");
                var text = search.GetType().GetProperty("Text");
                text.SetValue(search, "Ada", null); LivePump(150);
                PostKey(f, Keys.Enter);
                var first = opened.Count > 1 ? opened[1] : null;
                text.SetValue(search, "", null); LivePump(150);   // (cleared before the message search's half-second wait ends)
                check(first != null && first.PeerId == AdaId, "while searching, Enter opens the first result (" + (first != null ? first.Title : "none") + ")");
            }
            catch (Exception ex) { check(false, "the chat list check threw " + ex.GetType().Name + ": " + ex.Message); }
            finally { MainForm.OpenFromKeysHook = null; }

            UI.CountryPickerForm cp = null;
            try
            {
                cp = new UI.CountryPickerForm(ThemeHelper.IsDark, ThemeHelper.Palette.Accent) { StartPosition = FormStartPosition.Manual };
                cp.Location = new Point(f.Left + 40, f.Top + 40);
                cp.Show(f); LivePump(700);
                var cs = (Control)Field(cp, "_search");
                var list = (UI.Controls.ThemedListBox)Field(cp, "_list");
                var shown = (IList)Field(cp, "_shown");
                cs.Focus(); LivePump(200);
                PostKey(cp, Keys.Down); bool inList = list.ContainsFocus; int c0 = list.KeyCursor;
                PostKey(cp, Keys.Down); int c1 = list.KeyCursor;
                var shot = cp.Bounds; shot.Intersect(Screen.FromControl(cp).Bounds);
                if (shot.Width > 0 && shot.Height > 0)
                    using (var bmp = new Bitmap(shot.Width, shot.Height))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(shot.Location, Point.Empty, shot.Size);
                        bmp.Save(Path.Combine(_dir, "keys-list-ring.png"), ImageFormat.Png);
                    }
                PostKey(cp, Keys.Space);
                bool picked = cp.DialogResult == DialogResult.OK && ReferenceEquals(cp.SelectedCountry, shown[1]);
                check(inList && c0 == 0 && c1 == 1 && picked, "a dialog list: Down from its search box moves in, Down moves the ring, Space picks (in list: " + inList + ", rows " + c0 + ", " + c1 + ", picked row 1: " + picked + ")");
                cp.DialogResult = DialogResult.None;
                PostKey(cp, Keys.Up); PostKey(cp, Keys.Up);
                check(cs.ContainsFocus, "Up on the list's first row goes back to the search box");
                var canvas = (Control)Field(list, "_canvas");
                PostTap(canvas, new Point(canvas.Width / 2, 2 * list.RowHeight + list.RowHeight / 2));
                bool tapPicked = cp.DialogResult == DialogResult.OK && ReferenceEquals(cp.SelectedCountry, shown[2]);
                check(cs.ContainsFocus && tapPicked, "a tap on a row picks it and the focus stays in the search box (focus in box: " + cs.ContainsFocus + ", picked row 2: " + tapPicked + ")");
            }
            catch (Exception ex) { check(false, "the dialog list check threw " + ex.GetType().Name + ": " + ex.Message); }
            finally { if (cp != null) { try { cp.Close(); cp.Dispose(); } catch { } } }
            try { f.ActiveControl = null; } catch { }
            LivePump(300);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        /// <summary>A key as the keyboard sends it: posted to the window with the focus (WinForms' ordinary path).</summary>
        private static void PostKey(Form f, Keys k)
        {
            IntPtr target = GetFocus();
            if (target == IntPtr.Zero) target = f.Handle;
            PostMessage(target, 0x0100, (IntPtr)(int)k, (IntPtr)1);                           // WM_KEYDOWN
            PostMessage(target, 0x0101, (IntPtr)(int)k, (IntPtr)unchecked((int)0xC0000001));  // WM_KEYUP
            LivePump(250);
        }

        /// <summary>A left click at a client point, posted (a finger's tap arrives as the same two messages).</summary>
        private static void PostTap(Control c, Point p)
        {
            var lp = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
            PostMessage(c.Handle, 0x0201, (IntPtr)1, lp);   // WM_LBUTTONDOWN (MK_LBUTTON)
            PostMessage(c.Handle, 0x0202, IntPtr.Zero, lp); // WM_LBUTTONUP
            LivePump(300);
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
            if (area.Width <= 0 || area.Height <= 0) { _log.AppendLine("  " + name + ": the window is not on the screen (" + f.WindowState + " " + f.Bounds + "), no shot"); return; }
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

        /// <summary>Icon outlines at 16, 20, 24, 32 and 48 px in the theme's glyph colour (the offline preview before wiring).</summary>
        private static Form IconSheet(params string[] paths)
        {
            var f = new Form { FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(40 + 160 * 1, 30 + paths.Length * 60), BackColor = ThemeHelper.Palette.Window };
            int[] sizes = { 16, 20, 24, 32, 48 };
            f.ClientSize = new Size(20 + sizes.Length * 60, 20 + paths.Length * 60);
            f.Paint += (o, e) =>
            {
                for (int r = 0; r < paths.Length; r++)
                    for (int c = 0; c < sizes.Length; c++)
                        GroupCallWindow.FillIcon(e.Graphics, paths[r], new RectangleF(10 + c * 60 + (60 - sizes[c]) / 2f, 10 + r * 60 + (60 - sizes[c]) / 2f, sizes[c], sizes[c]),
                                                 ThemeHelper.Palette.AccentGlyph);
            };
            return f;
        }

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
