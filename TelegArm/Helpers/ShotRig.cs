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
            if (live == "callcam") return RunLiveCallCamera(dir);
            if (live == "calltest") return RunLiveCallTest(dir);
            if (live == "backdrop") return RunLiveBackdrop(dir);
            if (live == "teaser") return RunLiveTeaser(dir);

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
