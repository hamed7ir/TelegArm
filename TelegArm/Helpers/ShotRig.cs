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

            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                ThemeHelper.InitMode(mode);   // InitMode raises no ThemeChanged, so no live form re-themes mid-run
                // (Redesign 2.7a: the live app no longer creates MaterialSkin's manager — MainForm is an AppForm — so the rig
                //  stops creating it too. Its Roboto registration changed later text metrics; B1 was refreshed then.)
                string t = mode == ThemeMode.Light ? "light" : "dark";

                // The real first view is QR, but ShowView(Qr) starts a QR login (network). Phone is UI-only.
                WithForm("login-" + t, () => new LoginForm(new TelegramService()), f =>
                {
                    var view = typeof(LoginForm).GetNestedType("View", BindingFlags.NonPublic);
                    if (view == null) throw new MissingMemberException("LoginForm", "View");
                    Invoke(f, "ShowView", Enum.Parse(view, "Phone"));
                    Capture(f, "login-phone-" + t);
                });

                WithForm("main-" + t, () => new MainForm(new TelegramService()), f =>
                {
                    Capture(f, "main-empty-" + t);
                    SeedChatList(f);
                    Capture(f, "main-chats-" + t);
                    OpenDemoChat(f);
                    Capture(f, "main-chat-" + t);
                    // Plan E3: a five-line draft — the colour-emoji composer grows with it (Plain stays one row).
                    var ib = Field(f, "_messageInput");
                    ib.GetType().GetProperty("Enabled").SetValue(ib, true);
                    ib.GetType().GetProperty("Text").SetValue(ib, "Draft line one \U0001F600\nline two\nline three \u2764\uFE0F\nline four\nline five");
                    Settle(f);
                    Capture(f, "main-draft-" + t);
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
                WithForm("proxy-" + t, () => new ProxyForm(), f => Capture(f, "proxy-" + t));
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
                bmp.Save(Path.Combine(_dir, name + ".png"), ImageFormat.Png);
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
            foreach (var btn in new[] { "_chatSearchBtn", "_chatMenuBtn", "_dockBtn" })
            {
                var c = Field(main, btn) as Control;
                if (c != null) c.Visible = true;
            }
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
