using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TelegArm.Helpers;
using TL;

namespace TelegArm.Core
{
    /// <summary>
    /// Thin wrapper around WTelegramClient that owns the MTProto client and
    /// supplies the config callback Telegram uses to drive authentication.
    /// </summary>
    public class TelegramService
    {
        // API credentials (api_id/api_hash) live in the ApiCredentials partial class: real values in the
        // gitignored ApiCredentials.Local.cs, build placeholders in the committed ApiCredentials.cs. Kept
        // out of git entirely (see ApiCredentials.cs). Consumed only by Config() below.

        public const string SessionFileName = "TelegArm.session";
        public const string PhoneFileName = "TelegArm.phone";
        public const string UpdateStateFileName = "TelegArm.updates";   // UpdateManager pts/qts/seq/date (gap recovery)

        /// <summary>Session file for the ACTIVE account (accounts/{id}/session, or the legacy flat file
        /// during migration). Read by Config("session_pathname").</summary>
        public static string SessionPath => AccountContext.SessionPath;

        /// <summary>Stored phone for the active account (for silent resume's Config("phone_number")).</summary>
        public static string PhonePath => AccountContext.PhonePath;

        /// <summary>UpdateManager state file for the active account.</summary>
        public static string UpdateStatePath => AccountContext.UpdatePath;

        /// <summary>True when there's any account to resume (a multi-account dir or a legacy session).</summary>
        public static bool SessionExists => AccountStore.HasAnyAccountOrLegacy();

        private bool _silentResume;

        public WTelegram.Client Client { get; private set; }
        public User Me { get; private set; }

        /// <summary>True once the client holds a logged-in user (session is live).</summary>
        public bool IsAuthorized => Client?.User != null;

        /// <summary>
        /// Set during a login attempt when Telegram asks for a phone number that we
        /// don't have — i.e. there is no valid session and the user must sign in.
        /// </summary>
        public bool NeedsInteractiveLogin { get; private set; }

        /// <summary>
        /// The config callback WTelegramClient invokes for credentials and login input.
        /// </summary>
        private string Config(string what)
        {
            if (what == "api_id") return ApiCredentials.ApiId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (what == "api_hash") return ApiCredentials.ApiHash;
            if (what == "session_pathname") return SessionPath;
            // Device identity reported to Telegram (the session-list strings). Without these, WTelegram's
            // defaults produced the bogus "BlackBerry · Windows 10" — supply honest values instead. The app
            // NAME shown next to app_version comes from the api_id registration ("TelegArm"), so a release
            // session reads "TelegArm 0.9.0"; device_model is the secondary device line (the user's PC name,
            // as official clients show, falling back to "Desktop").
            if (what == "device_model") return string.IsNullOrWhiteSpace(Environment.MachineName) ? "Desktop" : Environment.MachineName;
            if (what == "system_version") return SystemVersion;
            if (what == "app_version") return Program.Version;   // AssemblyInfo 0.9.0.0 → "0.9.0"
            if (what == "lang_code") return "en";
            if (what == "system_lang_code") return "en";
            if (what == "phone_number")
            {
                // WTelegramClient always asks for the phone to drive login. With a
                // valid session it then logs in without a code; without a phone we
                // can't proceed, so an interactive sign-in is required.
                if (string.IsNullOrEmpty(AuthManager.PhoneNumber))
                    NeedsInteractiveLogin = true;
                return AuthManager.PhoneNumber;
            }
            if (what == "verification_code")
            {
                // A code request during silent resume means the session really is
                // expired — abort and let the LoginForm handle it interactively.
                if (_silentResume) { NeedsInteractiveLogin = true; return null; }
                return AuthManager.WaitForCode();
            }
            if (what == "password")
            {
                if (_silentResume) { NeedsInteractiveLogin = true; System.Diagnostics.Debug.WriteLine("[LOGIN] Config(password) → null (silent resume)"); return null; }
                var pwd = AuthManager.WaitForPassword();
                System.Diagnostics.Debug.WriteLine("[LOGIN] Config(password) returning " + (pwd != null ? pwd.Length : -1) + " chars");
                return pwd;
            }
            return null;
        }

        // ── Runtime OS version (for the device_model/system_version reported to Telegram) ──
        // Environment.OSVersion lies on an unmanifested app (caps at 6.2), so use RtlGetVersion (ntdll),
        // which returns the true major/minor/build regardless of the app's compatibility manifest.
        private static string _systemVersion;
        private static string SystemVersion
        {
            get { return _systemVersion ?? (_systemVersion = DetectWindowsVersion()); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RTL_OSVERSIONINFOW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW versionInfo);

        private static string DetectWindowsVersion()
        {
            try
            {
                var vi = new RTL_OSVERSIONINFOW();
                vi.dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(RTL_OSVERSIONINFOW));
                if (RtlGetVersion(ref vi) == 0)   // STATUS_SUCCESS
                {
                    uint maj = vi.dwMajorVersion, min = vi.dwMinorVersion, build = vi.dwBuildNumber;
                    bool arm = string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"),
                                             "ARM", StringComparison.OrdinalIgnoreCase);
                    if (maj == 10) return build >= 22000 ? "Windows 11" : "Windows 10";
                    if (maj == 6 && min == 3) return arm ? "Windows RT 8.1" : "Windows 8.1";
                    if (maj == 6 && min == 2) return "Windows 8";
                    if (maj == 6 && min == 1) return "Windows 7";
                    return "Windows " + maj + "." + min;
                }
            }
            catch { /* fall through to the managed best-effort */ }
            try { return "Windows " + Environment.OSVersion.Version.Major + "." + Environment.OSVersion.Version.Minor; }
            catch { return "Windows"; }
        }

        /// <summary>Persists the phone number so future sessions can resume silently.</summary>
        public void SavePhone(string phone)
        {
            try
            {
                if (!string.IsNullOrEmpty(phone))
                    File.WriteAllText(PhonePath, Security.Protect(phone.Trim()));
            }
            catch { /* non-fatal */ }
        }

        private static string LoadPhone()
        {
            try { return File.Exists(PhonePath) ? Security.Unprotect(File.ReadAllText(PhonePath).Trim()) : null; }
            catch { return null; }
        }

        /// <summary>
        /// Connects and logs in. When <paramref name="silentResume"/> is true, the
        /// stored phone is supplied automatically and any code/password request
        /// aborts (flagging NeedsInteractiveLogin) instead of blocking on the UI.
        /// </summary>
        public async Task<User> LoginAsync(bool silentResume = false)
        {
            _silentResume = silentResume;
            NeedsInteractiveLogin = false;

            // ALWAYS reload the active account's stored phone for a silent resume — NOT just when empty. A
            // prior interactive attempt (e.g. an abandoned add-account) leaves a STALE number in AuthManager;
            // reusing it when switching back to another account makes that account's resume need an
            // interactive login → a spurious "all accounts logged out" (the MULTI-fix wipe).
            if (silentResume)
                AuthManager.PhoneNumber = LoadPhone();

            EnsureClient();
            Me = await Client.LoginUserIfNeeded();
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            return Me;
        }

        /// <summary>Creates the resilient WTelegram client (shared by phone + QR login) if not already up.</summary>
        private void EnsureClient()
        {
            if (Client != null) return;
            Client = new WTelegram.Client(Config);
            // Country-blocked Telegram reached only via a flaky VPN tunnel → keep retrying on socket
            // errors instead of giving up after a few tries (the watchdog handles the black-hole case).
            Client.MaxAutoReconnects = 1000;
            TearingDown = false;   // a fresh client ends the teardown window (TEARDOWN-HYGIENE 1.2)
        }

        // ── TEARDOWN-HYGIENE: expected teardown races must not pollute crash.log ──

        /// <summary>TRUE from the moment OUR code starts disposing the client until the next EnsureClient —
        /// lets background-task observers classify faults as expected teardown races.</summary>
        public static volatile bool TearingDown;

        /// <summary>An EXPECTED lifetime fault: cancellation, or a disposed WTelegram client under the task.
        /// A1: an ObjectDisposedException naming WTelegram.Client is KNOWN-TRANSIENT even OUTSIDE a teardown
        /// window — the library disposes secondary-DC clients mid-session on its own (WTC FAQ).</summary>
        public static bool IsTeardownTransient(Exception ex)
        {
            if (ex is OperationCanceledException) return true;
            var ode = ex as ObjectDisposedException;
            if (ode == null) return false;
            if (TearingDown) return true;
            string name = ode.ObjectName ?? "";
            string msg = ode.Message ?? "";
            return name.Contains("WTelegram") || msg.Contains("WTelegram.Client");
        }

        /// <summary>Observes a fire-and-forget task (TEARDOWN-HYGIENE Part 2): expected lifetime races log ONE
        /// gated [TEARDOWN] line and stay out of crash.log (reserved for the unexpected); anything else is
        /// recorded (throttled). Lifetime observation only — the task's behavior is unchanged.</summary>
        public static void Observe(Task t, string context)
        {
            if (t == null) return;
            t.ContinueWith(x =>
            {
                var ex = x.Exception != null ? x.Exception.GetBaseException() : null;
                if (IsTeardownTransient(ex))
                {
                    if (TelegArm.Helpers.Logger.Enabled)
                        System.Diagnostics.Debug.WriteLine("[TEARDOWN] bg fetch lost client (transient) ctx=" + context);
                }
                else TelegArm.Helpers.CrashLog.RecordThrottled("bg-task:" + context, ex);
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>QR login (LoginWithQRCode): <paramref name="qrCodeUrl"/> is called with the tg://login URL to
        /// render (and re-called on refresh/expiry); the helper drives export→poll→migrate→success and, for a
        /// 2FA account, still asks the password via Config("password"). Cancel via <paramref name="ct"/>.</summary>
        public async Task<User> LoginWithQrAsync(Action<string> qrCodeUrl, CancellationToken ct)
        {
            // CRITICAL: this is an INTERACTIVE login. A prior failed silent resume (MainForm → LoginAsync(true))
            // leaves _silentResume == true on this shared service; without resetting it, Config("password")
            // would short-circuit to null for a 2FA account → "You must provide a config value for password".
            _silentResume = false;
            NeedsInteractiveLogin = false;
            EnsureClient();
            Me = await Client.LoginWithQRCode(qrCodeUrl, null, false, ct);   // false = don't log out an existing session
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            return Me;
        }

        // ── Update stream via UpdateManager (ordered delivery + getDifference gap recovery) ──
        /// <summary>The WTelegram UpdateManager: ordered updates, automatic getDifference on reconnect,
        /// and the Users/Chats entity dictionaries (UserOrChat). Null until <see cref="StartUpdateManager"/>.</summary>
        public WTelegram.UpdateManager Updates { get; private set; }

        /// <summary>
        /// Starts consuming updates through the UpdateManager: ordered per-update delivery to
        /// <paramref name="onUpdate"/>, resuming from (and gap-recovering against) the saved state file.
        /// reentrant=false → the manager waits for each callback before the next (we keep order).
        /// </summary>
        public void StartUpdateManager(Func<Update, Task> onUpdate)
        {
            if (Client == null || Updates != null) return;
            System.Diagnostics.Debug.WriteLine("[UM] StartUpdateManager: authorized user="
                + (Me != null ? Me.id.ToString() : "NULL") + " — attaching UpdateManager (post-login)");
            Updates = Client.WithUpdateManager(onUpdate, UpdateStatePath, null, false);
            System.Diagnostics.Debug.WriteLine("[UM] WithUpdateManager attached; state file=" + UpdateStatePath);
        }

        /// <summary>
        /// Seeds the manager's baseline update state (common pts/qts/date/seq + per-channel pts) via the
        /// documented LoadDialogs step. WITHOUT this the manager has no baseline, treats every live update as
        /// an unfillable gap, and delivers NOTHING — the root cause of "live updates dead after migration".
        /// Run after the chat list shows; the manager buffers / getDifference-recovers until it lands.
        /// </summary>
        public async Task SeedUpdateManagerAsync()
        {
            var mgr = Updates;
            if (mgr == null || Client == null) return;
            try
            {
                System.Diagnostics.Debug.WriteLine("[UM] seeding: Messages_GetAllDialogs → LoadDialogs…");
                var dialogs = await Client.Messages_GetAllDialogs();
                await mgr.LoadDialogs(dialogs, false);   // false = do NOT bulk-load unknown channels' full history
                System.Diagnostics.Debug.WriteLine("[UM] seeded: baseline update state established");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[UM] seed FAILED: " + ex.Message); }
        }

        // ── Connection watchdog (black-hole detection: VPN drops with WiFi still up) ──────────
        // When the VPN tunnel dies but WiFi stays up, the OS sees a live network, the TCP socket to
        // Telegram stays "open", and packets silently vanish — no socket error fires, so WTelegram's
        // auto-reconnect never triggers and the client waits forever on a dead socket. We detect this
        // by actively probing the link after a silence and force a reconnect if the probe times out.
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;   // last confirmed server activity (atomic via Interlocked)
        private int _reconnectingFlag;                              // 0/1 guard so only one reconnect runs at a time
        private Timer _watchdog;
        // Startup connect retry policy (the UI loops on these — connection failure at launch is NORMAL because
        // the VPN may not be up yet, so we retry forever with a capped backoff and never give up / never exit).
        public const int ConnectInitialBackoffMs = 3000;
        public const int ConnectMaxBackoffMs = 15000;
        // WTelegram's ConnectAsync/LoginUserIfNeeded take NO CancellationToken and BLOCK forever on a
        // black-holed socket (no network / VPN down). Every connect attempt is raced against this timeout so
        // a hung connect becomes a failed attempt that feeds the retry loop instead of wedging it.
        public const int ConnectAttemptTimeoutMs = 12000;

        /// <summary>Tears down a hung/in-flight connection (ResetAsync cancels the client's internal _cts,
        /// aborting a blocked ConnectAsync) WITHOUT forgetting the user/session, so the next attempt is clean.</summary>
        public async Task TeardownHungConnectAsync()
        {
            var client = Client;
            if (client == null) return;
            try { await client.ResetAsync(false, false).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[CONN] teardown error: " + ex.Message); }
        }

        // ── Contacts + group/channel creation (all OFF the UI thread + time-bounded; abandon on timeout —
        //    Telegram is VPN-only here, so a black-holed call must never hang the UI). ──

        private List<User> _contactsCache;   // fetched once per session; cleared on account teardown

        /// <summary>The user's contacts (Contacts_GetContacts), off-thread + bounded. Cached after the first
        /// fetch. Returns null on timeout/failure (caller shows a "couldn't reach Telegram" message).</summary>
        public async Task<List<User>> GetContactsAsync(int timeoutMs = 15000)
        {
            if (_contactsCache != null) return _contactsCache;
            var client = Client;
            if (client == null) return null;
            try
            {
                var task = Task.Run(() => client.Contacts_GetContacts(0));
                var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (done != task) { SwallowTaskFault(task); System.Diagnostics.Debug.WriteLine("[PEOPLE] contacts fetch TIMED OUT"); return null; }
                var res = await task.ConfigureAwait(false) as Contacts_Contacts;
                if (res == null) return new List<User>();
                _contactsCache = res.users.Values.OfType<User>().Where(u => u != null && !u.IsBot).ToList();
                System.Diagnostics.Debug.WriteLine("[PEOPLE] contacts fetched " + _contactsCache.Count + " off-thread");
                return _contactsCache;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[PEOPLE] contacts error: " + ex.Message); return null; }
        }

        /// <summary>Creates a SUPERGROUP (Channels_CreateChannel megagroup=true). Returns the new Channel or null.</summary>
        public Task<Channel> CreateSupergroupAsync(string title, string about, int timeoutMs = 20000)
        { return CreateChannelInternalAsync(title, about ?? "", true, timeoutMs); }

        /// <summary>Creates a broadcast CHANNEL (Channels_CreateChannel broadcast=true). Returns the new Channel or null.</summary>
        public Task<Channel> CreateBroadcastAsync(string title, string about, int timeoutMs = 20000)
        { return CreateChannelInternalAsync(title, about ?? "", false, timeoutMs); }

        private async Task<Channel> CreateChannelInternalAsync(string title, string about, bool megagroup, int timeoutMs)
        {
            var client = Client;
            if (client == null) return null;
            try
            {
                var task = Task.Run(() => megagroup
                    ? client.Channels_CreateChannel(title, about, megagroup: true)
                    : client.Channels_CreateChannel(title, about, broadcast: true));
                var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (done != task) { SwallowTaskFault(task); System.Diagnostics.Debug.WriteLine("[PEOPLE] create TIMED OUT"); return null; }
                var updates = await task.ConfigureAwait(false);
                var ch = updates.Chats.Values.OfType<Channel>().FirstOrDefault();
                System.Diagnostics.Debug.WriteLine("[PEOPLE] " + (megagroup ? "supergroup" : "channel") + " created id=" + (ch != null ? ch.id : 0));
                return ch;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[PEOPLE] create error: " + ex.Message); throw; }
        }

        /// <summary>Invites the given users to a freshly-created channel/supergroup, off-thread + bounded.</summary>
        public async Task InviteToChannelAsync(Channel channel, IEnumerable<User> users, int timeoutMs = 20000)
        {
            var client = Client;
            if (client == null || channel == null || users == null) return;
            var arr = users.Where(u => u != null).Select(u => (InputUserBase)new InputUser(u.id, u.access_hash)).ToArray();
            if (arr.Length == 0) return;
            var inputCh = new InputChannel(channel.id, channel.access_hash);
            try
            {
                var task = Task.Run(() => client.Channels_InviteToChannel(inputCh, arr));
                var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (done != task) { SwallowTaskFault(task); System.Diagnostics.Debug.WriteLine("[PEOPLE] invite TIMED OUT"); return; }
                await task.ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine("[PEOPLE] invited " + arr.Length + " members");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[PEOPLE] invite error: " + ex.Message); }
        }

        // ── Group/channel administration (ADMIN-manage) — every call off the UI thread + time-bounded ──────

        /// <summary>Runs a network op off the UI thread, bounded by a timeout. Timeout → default(T) (caller shows
        /// the "VPN" message); error → rethrow (caller shows the message). The black-hole rule for admin chains.</summary>
        private async Task<T> AdminBoundedAsync<T>(Func<Task<T>> op, int timeoutMs, string tag)
        {
            var client = Client;
            if (client == null) return default(T);
            try
            {
                var task = Task.Run(op);
                var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (done != task) { SwallowTaskFault(task); System.Diagnostics.Debug.WriteLine("[ADMIN] TIMEOUT " + tag); return default(T); }
                return await task.ConfigureAwait(false);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ADMIN] error " + tag + ": " + ex.Message); throw; }
        }

        private static InputChannel AsInputChannel(Channel ch) { return new InputChannel(ch.id, ch.access_hash); }
        private static InputPeerUser AsInputPeerUser(User u) { return new InputPeerUser(u.id, u.access_hash); }

        // TIER 1 — edit info + members
        public Task<bool> EditChatTitleAsync(Channel ch, string title, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Channels_EditTitle(AsInputChannel(ch), title ?? ""); return true; }, timeoutMs, "EditTitle");
        }

        public Task<bool> EditChatAboutAsync(InputPeer peer, string about, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(() => Client.Messages_EditChatAbout(peer, about ?? ""), timeoutMs, "EditAbout");
        }

        public Task<bool> EditChatPhotoAsync(Channel ch, string filePath, int timeoutMs = 60000)
        {
            return AdminBoundedAsync(async () =>
            {
                var file = await Client.UploadFileAsync(filePath, null);
                await Client.Channels_EditPhoto(AsInputChannel(ch),
                    new InputChatUploadedPhoto { file = file, flags = InputChatUploadedPhoto.Flags.has_file });
                return true;
            }, timeoutMs, "EditPhoto");
        }

        public Task<Channels_ChannelParticipants> GetParticipantsAsync(Channel ch, ChannelParticipantsFilter filter, int offset, int limit, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () =>
                await Client.Channels_GetParticipants(AsInputChannel(ch), filter, offset, limit, 0) as Channels_ChannelParticipants,
                timeoutMs, "GetParticipants");
        }

        /// <summary>Remove a member: ban (view_messages) then immediately unban → kicked but may rejoin.</summary>
        public Task<bool> KickMemberAsync(Channel ch, User user, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () =>
            {
                var inputCh = AsInputChannel(ch); var peer = AsInputPeerUser(user);
                await Client.Channels_EditBanned(inputCh, peer, new ChatBannedRights { flags = ChatBannedRights.Flags.view_messages });
                await Client.Channels_EditBanned(inputCh, peer, new ChatBannedRights { flags = 0 });
                return true;
            }, timeoutMs, "Kick");
        }

        // TIER 2 — admins / permissions / bans
        public Task<bool> SetAdminAsync(Channel ch, User user, ChatAdminRights rights, string rank, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Channels_EditAdmin(AsInputChannel(ch), new InputUser(user.id, user.access_hash), rights, rank ?? ""); return true; }, timeoutMs, "EditAdmin");
        }

        public Task<bool> SetDefaultPermissionsAsync(InputPeer peer, ChatBannedRights rights, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Messages_EditChatDefaultBannedRights(peer, rights); return true; }, timeoutMs, "DefaultPerms");
        }

        public Task<bool> BanMemberAsync(Channel ch, User user, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Channels_EditBanned(AsInputChannel(ch), AsInputPeerUser(user), new ChatBannedRights { flags = ChatBannedRights.Flags.view_messages }); return true; }, timeoutMs, "Ban");
        }

        public Task<bool> UnbanMemberAsync(Channel ch, User user, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Channels_EditBanned(AsInputChannel(ch), AsInputPeerUser(user), new ChatBannedRights { flags = 0 }); return true; }, timeoutMs, "Unban");
        }

        // TIER 3 — invite links / channel specifics
        public Task<ChatInviteExported> ExportInviteAsync(InputPeer peer, DateTime? expire, int? usageLimit, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () =>
                await Client.Messages_ExportChatInvite(peer, expire_date: expire, usage_limit: usageLimit) as ChatInviteExported,
                timeoutMs, "ExportInvite");
        }

        public Task<Messages_ExportedChatInvites> GetInvitesAsync(InputPeer peer, bool revoked, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(() => Client.Messages_GetExportedChatInvites(peer, null, 50, null, null, revoked), timeoutMs, "GetInvites");
        }

        public Task<bool> RevokeInviteAsync(InputPeer peer, string link, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Messages_EditExportedChatInvite(peer, link, revoked: true); return true; }, timeoutMs, "RevokeInvite");
        }

        public Task<bool> CheckUsernameAsync(Channel ch, string username, int timeoutMs = 15000)
        {
            return AdminBoundedAsync(() => Client.Channels_CheckUsername(AsInputChannel(ch), username ?? ""), timeoutMs, "CheckUsername");
        }

        public Task<bool> UpdateUsernameAsync(Channel ch, string username, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(() => Client.Channels_UpdateUsername(AsInputChannel(ch), username ?? ""), timeoutMs, "UpdateUsername");
        }

        public Task<bool> ToggleSignaturesAsync(Channel ch, bool on, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Channels_ToggleSignatures(AsInputChannel(ch), on, false); return true; }, timeoutMs, "ToggleSignatures");
        }

        /// <summary>Observes a faulted/abandoned Task so its exception can't surface as Unobserved.</summary>
        private static void SwallowTaskFault(Task t)
        {
            if (t == null) return;
            t.ContinueWith(x => { var ignore = x.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private const int WatchdogIntervalMs = 12000;   // re-armed after each tick (one-shot, no overlap)
        private const int ActivityStaleSeconds = 25;    // probe only after this much update-silence (pings keep it fresh)
        private const int ProbeTimeoutMs = 8000;        // the probe RPC must answer within this or the link is dead
        private const int ReconnectRetryMs = 5000;      // back-off between reconnect attempts while the VPN is still down

        /// <summary>True while a forced reconnect is in progress (drives the UI "Reconnecting…" hint).</summary>
        public bool IsReconnecting { get { return Volatile.Read(ref _reconnectingFlag) != 0; } }

        /// <summary>Raised (true) when a forced reconnect starts and (false) when it finishes.</summary>
        public event Action<bool> ReconnectingChanged;

        /// <summary>Marks "the server is alive": call on every received update and on probe/reconnect success.</summary>
        public void NoteActivity()
        {
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>Starts the periodic liveness watchdog. Safe to call once after login.</summary>
        public void StartConnectionWatchdog()
        {
            if (_watchdog != null || Client == null) return;
            NoteActivity();
            _watchdog = new Timer(WatchdogTick, null, WatchdogIntervalMs, Timeout.Infinite);   // one-shot; re-armed in the tick
            System.Diagnostics.Debug.WriteLine("[UM] watchdog started");
        }

        /// <summary>Stops + disposes the watchdog (call on shutdown).</summary>
        public void StopConnectionWatchdog()
        {
            var w = _watchdog; _watchdog = null;
            if (w != null) { try { w.Dispose(); } catch { } }
        }

        private async void WatchdogTick(object state)
        {
            try
            {
                if (Client == null || IsReconnecting) return;
                double idleSec = (DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc)).TotalSeconds;
                if (idleSec < ActivityStaleSeconds) return;   // recent update/ping → link is alive, nothing to do

                System.Diagnostics.Debug.WriteLine("[UM] watchdog: idle " + (int)idleSec + "s → probing link");
                bool alive = await ProbeAliveAsync().ConfigureAwait(false);
                if (alive)
                {
                    NoteActivity();
                    System.Diagnostics.Debug.WriteLine("[UM] watchdog: probe OK, link alive");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[UM] watchdog: link DEAD (probe timed out) → forcing reconnect");
                    await ForceReconnectAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[UM] watchdog EX: " + ex.Message); }
            finally
            {
                var w = _watchdog;
                if (w != null) { try { w.Change(WatchdogIntervalMs, Timeout.Infinite); } catch { } }   // re-arm (no overlap)
            }
        }

        /// <summary>Issues a cheap RPC with a short timeout. Completes ⇒ link alive; times out ⇒ black-holed.</summary>
        private async Task<bool> ProbeAliveAsync()
        {
            var client = Client;
            if (client == null) return false;
            try
            {
                var probe = client.Help_GetConfig();   // tiny round-trip to the home DC
                var winner = await Task.WhenAny(probe, Task.Delay(ProbeTimeoutMs)).ConfigureAwait(false);
                if (winner == probe)
                {
                    try { await probe.ConfigureAwait(false); return true; }
                    catch { return false; }   // RPC faulted → treat as dead (reconnect will heal)
                }
                // Timed out: don't leave the zombie probe task unobserved.
                var ignore = probe.ContinueWith(t => { var e = t.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Tears down the zombie socket and reconnects on the SAME session (no re-login): ResetAsync(false,false)
        /// keeps the logged-in user + secondary sessions; ConnectAsync() re-establishes the home-DC connection.
        /// The UpdateManager re-hooks the same client and resyncs (getDifference) as updates resume, so messages
        /// missed during the outage arrive. Retries with back-off until the VPN is back. One attempt-set at a time.
        /// </summary>
        private async Task ForceReconnectAsync()
        {
            if (Interlocked.CompareExchange(ref _reconnectingFlag, 1, 0) != 0) return;   // already reconnecting
            var handler = ReconnectingChanged; if (handler != null) handler(true);
            try
            {
                for (int attempt = 1; attempt <= 100; attempt++)
                {
                    var client = Client;
                    if (client == null) break;
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("[UM] reconnect attempt " + attempt + ": ResetAsync(false,false)+ConnectAsync");
                        await client.ResetAsync(false, false).ConfigureAwait(false);   // drop dead socket, KEEP user+session
                        // ConnectAsync can ALSO hang if the VPN is still down → bound it with the same timeout.
                        var connect = client.ConnectAsync();                           // fresh connection, same session
                        var done = await Task.WhenAny(connect, Task.Delay(ConnectAttemptTimeoutMs)).ConfigureAwait(false);
                        if (done != connect) { SwallowTaskFault(connect); throw new TimeoutException("ConnectAsync timed out (" + (ConnectAttemptTimeoutMs / 1000) + "s)"); }
                        await connect.ConfigureAwait(false);                           // observe success/exception
                        NoteActivity();                                                // manager resyncs as updates resume
                        System.Diagnostics.Debug.WriteLine("[UM] reconnected on attempt " + attempt + " (manager will getDifference)");
                        return;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[UM] reconnect attempt " + attempt + " failed: " + ex.Message + " — retrying in " + ReconnectRetryMs + "ms");
                        await Task.Delay(ReconnectRetryMs).ConfigureAwait(false);       // VPN may still be down → back off
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectingFlag, 0);
                var h2 = ReconnectingChanged; if (h2 != null) h2(false);
            }
        }

        /// <summary>Persists the manager's update state so a restart resumes (recovers) from where it left off.</summary>
        public void SaveUpdateState()
        {
            try { if (Updates != null) Updates.SaveState(UpdateStatePath); } catch { /* best-effort */ }
        }

        /// <summary>Fetches the user's dialog (chat) list. NOTE: limit 0 = ONE server-default page
        /// (~100 dialogs) — callers page the rest via <see cref="GetDialogsPageAsync"/>.</summary>
        public Task<Messages_DialogsBase> GetDialogsAsync()
        {
            return Client.Messages_GetDialogs();
        }

        /// <summary>One page of dialogs AFTER the given cursor (TL getDialogs pagination: the previous
        /// page's last dialog's top-message date/id/peer). limit 100 = the server's max page.</summary>
        public Task<Messages_DialogsBase> GetDialogsPageAsync(DateTime offsetDate, int offsetId, InputPeer offsetPeer)
        {
            return Client.Messages_GetDialogs(offsetDate, offsetId, offsetPeer, limit: 100);
        }

        /// <summary>One category's notify defaults (users / chats / broadcasts) — a peer with no explicit
        /// notify setting inherits these (NOTIFY-FIX 1.2). Null on failure (treated as not muted).</summary>
        public async Task<PeerNotifySettings> GetNotifyDefaultsAsync(InputNotifyPeerBase category)
        {
            var client = Client;
            if (client == null) return null;
            try { return await client.Account_GetNotifySettings(category).ConfigureAwait(false); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[NOTIFY] category defaults fetch failed (" + category.GetType().Name + "): " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Fetches message history for a peer. Pass <paramref name="offsetId"/> = the
        /// oldest message id already shown to page backwards; <paramref name="addOffset"/>
        /// (e.g. negative) loads messages around an anchor rather than strictly before it.
        /// </summary>
        public Task<Messages_MessagesBase> GetHistoryAsync(InputPeer peer, int limit = 50, int offsetId = 0, int addOffset = 0)
        {
            return Client.Messages_GetHistory(peer, offset_id: offsetId, add_offset: addOffset, limit: limit);
        }

        /// <summary>Re-syncs ONE dialog (authoritative top_message + unread_count + read watermarks + the top
        /// message itself) in a single call — used to refresh a chat-list row after a top-message deletion from
        /// another device (privacy) so the preview, badge, and date/order all reflect the server truth.</summary>
        public Task<Messages_PeerDialogs> GetPeerDialogAsync(InputPeer peer)
        {
            return Client.Messages_GetPeerDialogs(new InputDialogPeerBase[] { new InputDialogPeer { peer = peer } });
        }

        /// <summary>
        /// Global full-text message search. filter and offset_peer are left null —
        /// WTelegramClient serializes those as the API's "empty" constructors.
        /// </summary>
        public Task<Messages_MessagesBase> SearchMessagesAsync(string query, int limit = 50)
        {
            return Client.Messages_SearchGlobal(query, limit: limit);
        }

        /// <summary>Downloads a peer's small profile photo; returns its bytes (empty if none).</summary>
        public async Task<byte[]> DownloadAvatarAsync(IPeerInfo peer)
        {
            if (peer == null) return null;
            using (var ms = new MemoryStream())
            {
                try { await Client.DownloadProfilePhotoAsync(peer, ms, big: false); }
                catch { return null; }   // no photo / privacy / transient → fall back to letter
                return ms.ToArray();
            }
        }

        /// <summary>Downloads a peer's big profile photo (empty on failure).</summary>
        public async Task<byte[]> DownloadProfilePhotoBigAsync(IPeerInfo peer)
        {
            if (peer == null) return null;
            using (var ms = new MemoryStream())
            {
                try { await Client.DownloadProfilePhotoAsync(peer, ms, big: true); }
                catch { return null; }
                return ms.ToArray();
            }
        }

        /// <summary>Downloads a user's profile photos (newest first), for the full-photo viewer.</summary>
        public async Task<System.Collections.Generic.List<byte[]>> GetUserPhotosAsync(User u, int limit = 12)
        {
            var list = new System.Collections.Generic.List<byte[]>();
            if (u == null) return list;
            try
            {
                var res = await Client.Photos_GetUserPhotos(new InputUser(u.id, u.access_hash), 0, 0, limit);
                if (res?.photos != null)
                    foreach (var pb in res.photos)
                        if (pb is Photo p)
                            using (var ms = new MemoryStream())
                            {
                                await Client.DownloadFileAsync(p, ms, p.LargestPhotoSize);
                                list.Add(ms.ToArray());
                            }
            }
            catch { }
            return list;
        }

        /// <summary>Downloads a photo's smallest size (a few KB) for a quick preview thumbnail.</summary>
        public async Task<byte[]> DownloadPhotoThumbAsync(Photo photo)
        {
            var size = photo?.sizes?.OfType<PhotoSize>().OrderBy(s => s.size).FirstOrDefault();
            if (size == null) return null;
            using (var ms = new MemoryStream())
            {
                await Client.DownloadFileAsync(photo, ms, size);
                return ms.ToArray();
            }
        }

        /// <summary>Downloads a document's thumbnail: a real PhotoSize if present, else the inline
        /// cached-size bytes; null when neither is available.</summary>
        public async Task<byte[]> DownloadThumbAsync(Document doc)
        {
            if (doc?.thumbs == null) return null;
            var ps = doc.thumbs.OfType<PhotoSize>().OrderBy(s => s.size).LastOrDefault();
            if (ps != null)
                using (var ms = new MemoryStream())
                {
                    await Client.DownloadFileAsync(doc, ms, ps);
                    return ms.ToArray();
                }
            var cached = doc.thumbs.OfType<PhotoCachedSize>().LastOrDefault();
            return cached?.bytes;
        }

        /// <summary>Downloads a small document fully into memory (e.g. a sticker WEBP).</summary>
        public async Task<byte[]> DownloadDocBytesAsync(Document doc)
        {
            if (doc == null) return null;
            using (var ms = new MemoryStream())
            {
                await Client.DownloadFileAsync(doc, ms, (PhotoSizeBase)null);
                return ms.ToArray();
            }
        }

        /// <summary>True when a file at <paramref name="path"/> is COMPLETE: exists and (expected size unknown
        /// OR length matches). The size check is what heals partials the pre-DOWNLOAD-FIX code stranded at
        /// final paths — an existence-only check would accept them forever.</summary>
        public static bool IsFileComplete(string path, long expected)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                long len = new FileInfo(path).Length;
                return expected > 0 ? len == expected : len > 0;
            }
            catch { return false; }
        }

        private static bool DlLog => TelegArm.Helpers.Logger.Enabled;

        // In-flight direct-to-file fetches by doc id — a concurrent second request awaits the FIRST transfer
        // instead of opening a second writer on the same path (two writers = corruption).
        private readonly System.Collections.Generic.Dictionary<long, Task> _fileFetches
            = new System.Collections.Generic.Dictionary<long, Task>();

        /// <summary>Streams a document (video/gif/file) to disk, PARTIAL-PROOF: writes "&lt;path&gt;.part", verifies
        /// the byte count against Document.size, then atomically renames — nothing incomplete ever sits at the
        /// final path. Complete-at-final skips; a mismatched final is healed (deleted + re-fetched). Duplicate
        /// concurrent requests for the same doc id share one transfer. FILE_REFERENCE_EXPIRED retries once via
        /// <paramref name="refreshDoc"/> when provided. On failure the .part is kept (and discarded next start —
        /// v4.4.6 cannot resume: it always requests from offset 0, IL-verified).</summary>
        public Task DownloadDocumentToFileAsync(Document doc, string path, WTelegram.Client.ProgressCallback progress = null,
                                                Func<Task<Document>> refreshDoc = null)
        {
            Task t;
            lock (_dlGate)
            {
                // CROSS-DEDUP (CLEANUP-SAFE): a ring/handle transfer for this doc may already be writing — a
                // second writer on the same .part would throw a spurious FAILED. Join it: await the handle,
                // then ensure OUR path (usually the same file → instant; different path → sequential fetch).
                DownloadHandle inflight;
                if (_downloads.TryGetValue(doc.id, out inflight))
                {
                    if (DlLog) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] dedup id=" + doc.id + " joined in-flight (handle)");
                    return JoinHandleThenEnsure(inflight, doc, path, progress, refreshDoc);
                }
                if (_fileFetches.TryGetValue(doc.id, out t))
                {
                    if (DlLog) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] dedup id=" + doc.id + " joined in-flight");
                    return t;   // dedup: await the in-flight transfer
                }
                t = DownloadDocumentCore(doc, path, progress, refreshDoc);
                _fileFetches[doc.id] = t;
            }
            t.ContinueWith(_ => { lock (_dlGate) { _fileFetches.Remove(doc.id); } });
            return t;
        }

        /// <summary>Dedup join: wait for the in-flight HANDLE transfer, then ensure our requested path.
        /// CANCEL SEMANTICS (by design): if the primary was cancelled by the user, the joiner ABORTS cleanly
        /// (OperationCanceledException) instead of silently restarting the transfer the user just stopped.</summary>
        private async Task JoinHandleThenEnsure(DownloadHandle h, Document doc, string path,
                                                WTelegram.Client.ProgressCallback progress, Func<Task<Document>> refreshDoc)
        {
            await h.Completion.ConfigureAwait(false);
            if (h.State == DownloadHandle.DState.Cancelled)
            {
                if (DlLog) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] dedup joiner aborted (primary cancelled) id=" + doc.id);
                throw new OperationCanceledException("primary download cancelled");
            }
            if (IsFileComplete(path, doc.size)) return;                       // primary produced our file
            await DownloadDocumentToFileAsync(doc, path, progress, refreshDoc).ConfigureAwait(false);   // sequential — primary has finished
        }

        private async Task DownloadDocumentCore(Document doc, string path, WTelegram.Client.ProgressCallback progress,
                                                Func<Task<Document>> refreshDoc)
        {
            long expected = doc.size;
            if (IsFileComplete(path, expected)) return;   // complete, verified → nothing to do
            if (File.Exists(path))
            {
                if (DlLog) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] heal id=" + doc.id + " onDisk=" + new FileInfo(path).Length + " expected=" + expected + " → re-download");
                try { File.Delete(path); } catch { }
            }
            string part = path + ".part";
            if (File.Exists(part))
            {
                if (DlLog) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] stale part discarded id=" + doc.id + " (direct fetch — no resume path)");
                try { File.Delete(part); } catch { }
            }
            if (DlLog) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] start id=" + doc.id + " size=" + expected + " path=" + path);

            long lastPct = -1; DateTime lastAt = DateTime.MinValue;
            WTelegram.Client.ProgressCallback wrapped = (t, tot) =>
            {
                if (DlLog && tot > 0)
                {
                    long pct = t * 100 / tot;   // HOT: throttle to every ~10% or 2s
                    if (pct >= lastPct + 10 || (DateTime.UtcNow - lastAt).TotalSeconds >= 2)
                    { lastPct = pct; lastAt = DateTime.UtcNow; System.Diagnostics.Debug.WriteLine("[DOWNLOAD] progress id=" + doc.id + " got=" + t + "/" + tot); }
                }
                if (progress != null) progress(t, tot);
            };

            var docNow = doc; bool refreshed = false;
            for (; ; )
            {
                try
                {
                    using (var fs = File.Create(part))
                        await Client.DownloadFileAsync(docNow, fs, (PhotoSizeBase)null, wrapped);
                    break;
                }
                catch (RpcException rex) when (!refreshed && refreshDoc != null
                                               && rex.Message != null && rex.Message.Contains("FILE_REFERENCE"))
                {
                    refreshed = true;
                    if (DlLog) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] FILE_REFERENCE expired id=" + doc.id + " → refreshing + retry once");
                    var fresh = await refreshDoc();
                    if (fresh == null) throw;
                    docNow = fresh;
                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                }
                catch (Exception ex)
                {
                    if (DlLog) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] FAILED id=" + doc.id + " got=?/" + expected
                        + " ex=" + ex.GetType().Name + ":" + ex.Message + " part=kept");
                    throw;
                }
            }

            long written = File.Exists(part) ? new FileInfo(part).Length : -1;
            bool match = expected <= 0 ? written > 0 : written == expected;
            if (!match)
            {
                if (DlLog) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] FAILED id=" + doc.id + " got=" + written + "/" + expected + " ex=IncompleteTransfer part=kept");
                throw new IOException("incomplete transfer: wrote " + written + " of " + expected);
            }
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            File.Move(part, path);
            if (DlLog) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] done id=" + doc.id + " wrote=" + written + " expected=" + expected + " match=Y → renamed");
        }

        /// <summary>Re-fetches a message and returns its (fresh-referenced) Document — the FILE_REFERENCE_EXPIRED
        /// remedy. Null when unavailable.</summary>
        public async Task<Document> RefetchDocumentAsync(InputPeer peer, int msgId)
        {
            try
            {
                if (peer == null) return null;
                var res = await Client.GetMessages(peer, msgId);
                var msg = res?.Messages?.OfType<Message>().FirstOrDefault(m => m.id == msgId);
                return (msg?.media as MessageMediaDocument)?.document as Document;
            }
            catch { return null; }
        }

        // ── Cancellable in-bubble downloads (progress ring + ✕) ──────────────
        private readonly System.Collections.Generic.Dictionary<long, DownloadHandle> _downloads
            = new System.Collections.Generic.Dictionary<long, DownloadHandle>();
        private readonly object _dlGate = new object();

        /// <summary>One roster row per TRACKED transfer this session (active, paused, done, failed) — the
        /// downloads-manager panel consumes this; it outlives bubbles and chats (DOWNLOAD-UX).</summary>
        public sealed class TransferInfo
        {
            public long DocId; public string FileName, ChatTitle, Path;
            public Document Doc; public Func<Task<Document>> RefreshDoc;
            public DownloadHandle Handle;   // live while transferring; kept afterwards for its final state
            /// <summary>False for small policy auto-downloads — tracked (dedup/background) but hidden from
            /// the manager panel + indicator badge (DOWNLOAD-UX v3 1.2). Upgraded to true if re-requested
            /// user-initiated or ≥ the visibility size floor.</summary>
            public bool PanelVisible = true;
            /// <summary>Media class (audio|video|gif|voice|doc) for the panel + logs.</summary>
            public string Type = "doc";
        }
        private readonly System.Collections.Generic.Dictionary<long, TransferInfo> _roster
            = new System.Collections.Generic.Dictionary<long, TransferInfo>();

        /// <summary>Roster changed (transfer added / reached a terminal state / removed). Any thread.</summary>
        public event Action TransfersChanged;
        private void RaiseTransfersChanged() { var h = TransfersChanged; if (h != null) { try { h(); } catch { } } }

        /// <summary>Snapshot of the session's tracked transfers (manager rows).</summary>
        public TransferInfo[] SnapshotTransfers()
        {
            lock (_dlGate) { var a = new TransferInfo[_roster.Count]; _roster.Values.CopyTo(a, 0); return a; }
        }

        /// <summary>The roster entry for a doc id (active OR finished), or null.</summary>
        public TransferInfo GetTransfer(long docId)
        {
            lock (_dlGate) { TransferInfo ti; return _roster.TryGetValue(docId, out ti) ? ti : null; }
        }

        /// <summary>Starts (or returns the existing) download for a document id. One handle per id — a second
        /// tap returns the in-flight one instead of starting a duplicate. Cancel/Pause via the handle.
        /// <paramref name="track"/>=false keeps it out of the manager roster (voice notes, thumbs).</summary>
        public DownloadHandle StartDocumentDownload(Document doc, string path, Func<Task<Document>> refreshDoc = null,
                                                    string fileName = null, string chatTitle = null, bool track = true,
                                                    bool panelVisible = true, string type = "doc")
        {
            if (doc == null || Client == null) return null;
            DownloadHandle h;
            lock (_dlGate)
            {
                if (_downloads.TryGetValue(doc.id, out h))
                {
                    // DOWNLOAD-RESUME: a paused handle stays registered — any "start" request on it is a
                    // SAME-HANDLE resume (bubble tap, panel button, next-launch re-request all converge here).
                    if (h.State == DownloadHandle.DState.Paused) h.Resume();
                    TransferInfo up;
                    if (panelVisible && _roster.TryGetValue(doc.id, out up) && !up.PanelVisible)
                        up.PanelVisible = true;   // a user-initiated re-request surfaces a hidden auto-download
                    return h;   // dedup (handle path)
                }
                h = new DownloadHandle(doc.id, path) { TypeTag = type };
                _downloads[doc.id] = h;
                if (track)
                {
                    TransferInfo ti;
                    if (!_roster.TryGetValue(doc.id, out ti))
                        _roster[doc.id] = ti = new TransferInfo { DocId = doc.id, PanelVisible = panelVisible };
                    else if (panelVisible) ti.PanelVisible = true;   // upgrade only — hidden stays hidden until user-initiated
                    ti.Doc = doc; ti.Path = path; ti.Handle = h;
                    ti.RefreshDoc = refreshDoc ?? ti.RefreshDoc;
                    ti.FileName = fileName ?? ti.FileName ?? System.IO.Path.GetFileName(path);
                    ti.ChatTitle = chatTitle ?? ti.ChatTitle;
                    ti.Type = type;
                }
                // CROSS-DEDUP (CLEANUP-SAFE): a direct fetch for this doc may already be writing. Defer this
                // handle's Run until it finishes — Run's early-out then completes instantly if the direct fetch
                // produced our file, else downloads sequentially. No two writers can share a .part.
                Task direct;
                if (_fileFetches.TryGetValue(doc.id, out direct))
                {
                    if (DlLog) System.Diagnostics.Debug.WriteLine("[DOWNLOAD] dedup id=" + doc.id + " joined in-flight (deferred handle)");
                    direct.ContinueWith(_ => h.Run(Client, doc, OnDownloadDone, refreshDoc));
                    RaiseTransfersChanged();
                    return h;
                }
            }
            h.Run(Client, doc, OnDownloadDone, refreshDoc);
            RaiseTransfersChanged();
            return h;
        }

        private void OnDownloadDone(DownloadHandle h)
        {
            lock (_dlGate) { _downloads.Remove(h.DocId); }
            System.Diagnostics.Debug.WriteLine("[DL] " + h.State + " id=" + h.DocId);
            RaiseTransfersChanged();
        }

        /// <summary>The in-flight download for a document id, or null.</summary>
        public DownloadHandle GetDownload(long docId)
        {
            lock (_dlGate) { DownloadHandle h; return _downloads.TryGetValue(docId, out h) ? h : null; }
        }

        /// <summary>Pauses a transfer (mechanism (b): abort-keep-part; resume is a fresh transfer).</summary>
        public void PauseDownload(long docId) { var h = GetDownload(docId); if (h != null) h.Pause(); }

        /// <summary>Resumes a transfer. Paused → SAME-HANDLE resume (raw Upload_GetFile loop from the verified
        /// offset — DOWNLOAD-RESUME). Failed/cancelled roster rows → a fresh handle whose Attempt resumes from
        /// the part+sidecar when present, else refetches.</summary>
        public DownloadHandle ResumeDownload(long docId)
        {
            var live = GetDownload(docId);
            if (live != null)
            {
                if (live.State == DownloadHandle.DState.Paused) live.Resume();
                return live;   // downloading → idempotent no-op
            }
            var ti = GetTransfer(docId);
            if (ti == null || ti.Doc == null) return null;
            return StartDocumentDownload(ti.Doc, ti.Path, ti.RefreshDoc, ti.FileName, ti.ChatTitle);
        }

        /// <summary>Cancels a transfer: aborts it when active, else drops the roster row (+ stale .part).</summary>
        public void CancelTransfer(long docId)
        {
            var h = GetDownload(docId);
            if (h != null) { h.Cancel(); return; }
            TransferInfo ti;
            lock (_dlGate) { if (_roster.TryGetValue(docId, out ti)) _roster.Remove(docId); }
            if (ti != null) { try { if (File.Exists(ti.Path + ".part")) File.Delete(ti.Path + ".part"); } catch { } }
            RaiseTransfersChanged();
        }

        /// <summary>Drops finished (done/failed/cancelled) rows from the manager roster.</summary>
        public void ClearFinishedTransfers()
        {
            lock (_dlGate)
            {
                var dead = new System.Collections.Generic.List<long>();
                foreach (var kv in _roster)
                    if (kv.Value.Handle == null || (kv.Value.Handle.State != DownloadHandle.DState.Downloading
                                                    && kv.Value.Handle.State != DownloadHandle.DState.Paused))
                        dead.Add(kv.Key);
                foreach (var id in dead) _roster.Remove(id);
            }
            RaiseTransfersChanged();
        }

        /// <summary>Cancels every in-flight download. DOWNLOAD-UX POLICY: chat switches NO LONGER call this —
        /// downloads run in the background across chats. It remains for ACCOUNT teardown (cache-isolation:
        /// a cross-account transfer writing into a switched cache root would corrupt) and clears the roster.</summary>
        public void CancelAllDownloads(string reason = "account-switch")
        {
            DownloadHandle[] all;
            lock (_dlGate)
            {
                all = new DownloadHandle[_downloads.Count]; _downloads.Values.CopyTo(all, 0);
                _roster.Clear();   // per-account roster
            }
            foreach (var h in all) { try { h.Cancel(reason); } catch { } }
            if (all.Length > 0) System.Diagnostics.Debug.WriteLine("[DL] " + reason + " cancelled " + all.Length + " downloads");
            RaiseTransfersChanged();
        }

        /// <summary>Marks the conversation read up to <paramref name="maxId"/> (best-effort).</summary>
        public Task ReadHistoryAsync(InputPeer peer, int maxId)
        {
            if (peer is InputPeerChannel ch)
                return Client.Channels_ReadHistory(new InputChannel(ch.channel_id, ch.access_hash), maxId);
            return Client.Messages_ReadHistory(peer, maxId);
        }

        // ── Picker metadata cache ────────────────────────────────────────────
        // The picker (EmojiPicker) is recreated on every open, so WITHOUT this its metadata RPCs
        // (recent/faved/all-sets/saved-gifs) re-hit the network EVERY open — the VPN-bound "slow every open".
        // Cache-first: an open with a cached value returns INSTANTLY (no await on the network); a value older
        // than MetaStale triggers a NON-BLOCKING background refresh. This instance is reused across accounts,
        // so TeardownForSwitchAsync clears it (per-account correctness). [PICKER]-logged for diagnosis.
        private sealed class MetaCache<T> where T : class { public T Value; public DateTime At; public bool Refreshing; }
        private readonly MetaCache<Document[]> _recentStkCache = new MetaCache<Document[]>();
        private readonly MetaCache<Document[]> _favedStkCache = new MetaCache<Document[]>();
        private readonly MetaCache<StickerSet[]> _allSetsCache = new MetaCache<StickerSet[]>();
        private readonly MetaCache<Document[]> _savedGifsCache = new MetaCache<Document[]>();
        private static readonly TimeSpan MetaStale = TimeSpan.FromMinutes(5);
        private static readonly Document[] EmptyDocs = new Document[0];
        private static readonly StickerSet[] EmptySets = new StickerSet[0];

        /// <summary>Clears the picker metadata cache (called on account switch — the next open re-fetches for the new account).</summary>
        public void ClearPickerMetaCache()
        {
            _recentStkCache.Value = null; _favedStkCache.Value = null;
            _allSetsCache.Value = null; _savedGifsCache.Value = null;
        }

        private async Task<T> CachedMeta<T>(MetaCache<T> c, string name, Func<Task<T>> fetch, T empty) where T : class
        {
            if (c.Value != null)   // instant open: return the cache, refresh in the background only if stale
            {
                if (!c.Refreshing && (DateTime.UtcNow - c.At) > MetaStale) { c.Refreshing = true; var _ = RefreshMeta(c, name, fetch); }
                System.Diagnostics.Debug.WriteLine("[PICKER] " + name + " cache HIT (n=" + ((c.Value as Array)?.Length ?? 0) + ")");
                return c.Value;
            }
            System.Diagnostics.Debug.WriteLine("[PICKER] " + name + " cache MISS -> network fetch");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            T v; try { v = await fetch(); } catch { v = empty; }
            c.Value = v ?? empty; c.At = DateTime.UtcNow;
            System.Diagnostics.Debug.WriteLine("[PICKER] " + name + " fetched in " + sw.ElapsedMilliseconds + "ms (n=" + ((c.Value as Array)?.Length ?? 0) + ")");
            return c.Value;
        }

        private async Task RefreshMeta<T>(MetaCache<T> c, string name, Func<Task<T>> fetch) where T : class
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { var v = await fetch(); if (v != null) { c.Value = v; c.At = DateTime.UtcNow; } System.Diagnostics.Debug.WriteLine("[PICKER] " + name + " bg-refresh " + sw.ElapsedMilliseconds + "ms"); }
            catch { }
            finally { c.Refreshing = false; }
        }

        /// <summary>The user's recent stickers (cache-first; empty on failure).</summary>
        public Task<Document[]> GetRecentStickersAsync() => CachedMeta(_recentStkCache, "recent", FetchRecentStickers, EmptyDocs);
        private async Task<Document[]> FetchRecentStickers()
        { var r = await Client.Messages_GetRecentStickers(); return (r as Messages_RecentStickers)?.stickers.OfType<Document>().ToArray() ?? EmptyDocs; }

        /// <summary>The user's favorite stickers (cache-first).</summary>
        public Task<Document[]> GetFavedStickersAsync() => CachedMeta(_favedStkCache, "faved", FetchFavedStickers, EmptyDocs);
        private async Task<Document[]> FetchFavedStickers()
        { var r = await Client.Messages_GetFavedStickers(); return (r as Messages_FavedStickers)?.stickers.OfType<Document>().ToArray() ?? EmptyDocs; }

        /// <summary>The user's installed sticker sets (cache-first).</summary>
        public Task<StickerSet[]> GetStickerSetsAsync() => CachedMeta(_allSetsCache, "sets", FetchStickerSets, EmptySets);
        private async Task<StickerSet[]> FetchStickerSets()
        { var r = await Client.Messages_GetAllStickers(); return (r as Messages_AllStickers)?.sets ?? EmptySets; }

        /// <summary>Pins a message (pm_oneside=true pins only on our side in private chats).</summary>
        public Task PinMessageAsync(InputPeer peer, int id, bool oneside)
            => Client.Messages_UpdatePinnedMessage(peer, id, pm_oneside: oneside);

        /// <summary>Unpins a message.</summary>
        public Task UnpinMessageAsync(InputPeer peer, int id)
            => Client.Messages_UpdatePinnedMessage(peer, id, unpin: true);

        /// <summary>Fetches custom-emoji documents by id (batch).</summary>
        public async Task<Document[]> GetCustomEmojiDocsAsync(long[] ids)
        {
            try { var r = await Client.Messages_GetCustomEmojiDocuments(ids); return r?.OfType<Document>().ToArray() ?? new Document[0]; }
            catch { return new Document[0]; }
        }

        /// <summary>Inspects an invite hash (preview vs already-a-member) without joining.</summary>
        public Task<ChatInviteBase> CheckInviteAsync(string hash)
            => Client.Messages_CheckChatInvite(hash);

        /// <summary>Joins a chat/channel by invite hash; the chat then resolves via CheckInviteAsync.</summary>
        public Task<UpdatesBase> JoinInviteAsync(string hash)
            => Client.Messages_ImportChatInvite(hash);

        /// <summary>Resolves a public @username to its User/ChatBase (null if not found).</summary>
        public async Task<IPeerInfo> ResolveUsernameAsync(string username)
        {
            try
            {
                var r = await Client.Contacts_ResolveUsername(username);
                if (r == null) return null;
                switch (r.peer)
                {
                    case PeerUser pu: return r.users.TryGetValue(pu.user_id, out var u) ? (IPeerInfo)u : null;
                    case PeerChannel pc: return r.chats.TryGetValue(pc.channel_id, out var c) ? (IPeerInfo)c : null;
                    case PeerChat pch: return r.chats.TryGetValue(pch.chat_id, out var c2) ? (IPeerInfo)c2 : null;
                    default: return null;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// The COMPLETE ordered set of a chat's pinned messages (newest→oldest). Pages the
        /// InputMessagesFilterPinned search to completion — a single page can be short even when more
        /// pins exist, which is what made the bar stop cycling at the 2nd/3rd pin. The same search works
        /// for users, basic groups and channels (the result type differs — Messages_Messages vs
        /// Messages_ChannelMessages — but .Messages carries the pinned messages in every case).
        /// </summary>
        public async Task<Message[]> GetPinnedMessagesAsync(InputPeer peer)
        {
            var all = new List<Message>();
            var seen = new HashSet<int>();
            try
            {
                int offsetId = 0;
                for (int page = 0; page < 20; page++)   // safety cap: 20×100 = 2000 pins
                {
                    var r = await Client.Messages_Search(peer, "", new InputMessagesFilterPinned(), offset_id: offsetId, limit: 100);
                    var msgs = r?.Messages?.OfType<Message>().ToList();
                    System.Diagnostics.Debug.WriteLine("[PIN] search page " + page + " offset_id=" + offsetId
                        + " returned=" + (msgs?.Count ?? 0));
                    if (msgs == null || msgs.Count == 0) break;
                    int added = 0;
                    foreach (var m in msgs) if (seen.Add(m.ID)) { all.Add(m); added++; }
                    offsetId = msgs.Min(m => m.ID);   // next page: pins OLDER than the smallest we've seen
                    if (added == 0 || msgs.Count < 100) break;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[PIN] search failed: " + ex.Message); }
            System.Diagnostics.Debug.WriteLine("[PIN] fetch total=" + all.Count);
            return all.OrderByDescending(m => m.ID).ToArray();   // newest → oldest
        }

        /// <summary>Unpins a message; forEveryone uses the normal unpin (needs pin rights in groups/channels),
        /// otherwise pm_oneside hides it only on our side in a private chat.</summary>
        public Task UnpinMessageAsync(InputPeer peer, int id, bool forEveryone)
            => Client.Messages_UpdatePinnedMessage(peer, id, unpin: true, pm_oneside: !forEveryone);

        /// <summary>Archived dialogs (folder_id = 1).</summary>
        public Task<Messages_DialogsBase> GetArchivedDialogsAsync()
            => Client.Messages_GetDialogs(folder_id: 1);

        /// <summary>Moves a chat to a folder: 1 = Archive, 0 = main list (unarchive).</summary>
        public Task SetChatFolderAsync(InputPeer peer, int folderId)
            => Client.Folders_EditPeerFolders(new[] { new InputFolderPeer { peer = peer, folder_id = folderId } });

        /// <summary>Searches a peer's history for media of a given filter (paged via offsetId).</summary>
        public Task<Messages_MessagesBase> SearchPeerMediaAsync(InputPeer peer, MessagesFilter filter, int offsetId, int limit)
            => Client.Messages_Search(peer, "", filter, offset_id: offsetId, limit: limit);

        /// <summary>Per-filter message counts for a peer in one round-trip (messages.getSearchCounters).</summary>
        public async Task<Messages_SearchCounter[]> GetMediaCountsAsync(InputPeer peer, MessagesFilter[] filters)
        {
            try { return await Client.Messages_GetSearchCounters(peer, filters); }
            catch { return new Messages_SearchCounter[0]; }
        }

        /// <summary>Mutes/unmutes a peer's notifications (account.updateNotifySettings). The TL flag bit MUST
        /// accompany the value: WriteTL serializes mute_until ONLY when flags.has_mute_until is set (IL-proven
        /// in the packaged 4.4.6), so the old flag-less call was a server-side NO-OP — every TelegArm mute
        /// silently reverted on relaunch (MUTE-PERSIST). Returns the server's bool; callers commit the local
        /// state only on success.</summary>
        public Task<bool> ToggleMuteAsync(InputPeer peer, bool mute)
            => Client.Account_UpdateNotifySettings(
                   new InputNotifyPeer { peer = peer },
                   new InputPeerNotifySettings
                   {
                       flags = InputPeerNotifySettings.Flags.has_mute_until,
                       mute_until = mute ? DateTime.UtcNow.AddYears(10) : new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                   });

        /// <summary>Blocks/unblocks a user (contacts.block / contacts.unblock).</summary>
        public Task SetBlockedAsync(InputPeer peer, bool blocked)
            => blocked ? (Task)Client.Contacts_Block(peer) : Client.Contacts_Unblock(peer);

        /// <summary>Joins a channel/supergroup (channels.joinChannel). Returns the updates.</summary>
        public Task<UpdatesBase> JoinChannelAsync(InputPeerChannel peer)
            => Client.Channels_JoinChannel(new InputChannel(peer.channel_id, peer.access_hash));

        /// <summary>Full user info (for the composer-footer "blocked" flag); null on failure.</summary>
        public async Task<UserFull> GetUserFullAsync(User u)
        {
            try { var f = await Client.Users_GetFullUser(new InputUser(u.id, u.access_hash)); return f?.full_user; }
            catch { return null; }
        }

        // ── Privacy & Security (account.getPrivacy / account.setPrivacy) ────
        /// <summary>The simplified "primary" privacy setting (the per-user exception lists are ignored — v1).</summary>
        public enum PrivacyPrimary { Everybody, Contacts, Nobody }

        /// <summary>Raw privacy rules for a key (account.getPrivacy). InputPrivacyKey is an enum in 4.4.6.</summary>
        public Task<Account_PrivacyRules> GetPrivacyAsync(InputPrivacyKey key)
            => Client.Account_GetPrivacy(key);

        /// <summary>Reduces a returned rule vector to its primary value (broadest allow wins). Per-user
        /// allow/disallow exception lists are NOT surfaced in v1.</summary>
        public static PrivacyPrimary ReducePrivacy(PrivacyRule[] rules)
        {
            if (rules != null)
            {
                bool allowAll = false, allowContacts = false;
                foreach (var r in rules)
                {
                    if (r is PrivacyValueAllowAll) allowAll = true;
                    else if (r is PrivacyValueAllowContacts) allowContacts = true;
                }
                if (allowAll) return PrivacyPrimary.Everybody;
                if (allowContacts) return PrivacyPrimary.Contacts;
            }
            return PrivacyPrimary.Nobody;
        }

        /// <summary>Sets a key's primary privacy value (account.setPrivacy). v1 sends ONLY the primary rule —
        /// any existing per-user exception lists are replaced. Returns the server's resulting rules.</summary>
        public Task<Account_PrivacyRules> SetPrivacyAsync(InputPrivacyKey key, PrivacyPrimary value)
        {
            InputPrivacyRule[] rules;
            if (value == PrivacyPrimary.Everybody) rules = new InputPrivacyRule[] { new InputPrivacyValueAllowAll() };
            else if (value == PrivacyPrimary.Contacts) rules = new InputPrivacyRule[] { new InputPrivacyValueAllowContacts() };
            else rules = new InputPrivacyRule[] { new InputPrivacyValueDisallowAll() };
            return Client.Account_SetPrivacy(key, rules);
        }

        // ── Two-step verification / cloud password (account.* + SRP helper) ──
        // ALL crypto goes through WTelegram's SRP helper Client.InputCheckPassword(Account_Password,string):
        //  • current_algo SET  → returns the current-password challenge proof (srp_id/A/M1).
        //  • current_algo NULL → returns the NEW-password verifier g^x mod p in field .A (used as new_password_hash).
        // (Confirmed by decompiling WTelegram.Encryption.Check2FA.) NEVER hand-roll SRP.

        /// <summary>Current 2FA config (account.getPassword). Re-fetch before each SRP build (srp_B/srp_id are per-request).</summary>
        public Task<Account_Password> GetPasswordAsync() => Client.Account_GetPassword();

        /// <summary>Derives new_password_hash (the SRP verifier .A) for a NEW password via the library helper.</summary>
        private async Task<byte[]> NewPasswordHashAsync(Account_Password pwd, string newPassword)
        {
            var tmp = new Account_Password { new_algo = pwd.new_algo };   // current_algo null → helper returns verifier in .A
            var srp = await WTelegram.Client.InputCheckPassword(tmp, newPassword);
            return srp.A;
        }

        /// <summary>Enables 2FA when none is set: builds the verifier and updates with an EMPTY current-password.</summary>
        public async Task SetPasswordAsync(string newPassword, string hint, string email)
        {
            var pwd = await Client.Account_GetPassword();
            var hash = await NewPasswordHashAsync(pwd, newPassword);
            var settings = new Account_PasswordInputSettings
            {
                flags = Account_PasswordInputSettings.Flags.has_new_algo,   // covers new_algo + new_password_hash + hint
                new_algo = pwd.new_algo,
                new_password_hash = hash,
                hint = hint ?? ""
            };
            if (!string.IsNullOrEmpty(email))
            {
                settings.email = email;
                settings.flags |= Account_PasswordInputSettings.Flags.has_email;
            }
            await Client.Account_UpdatePasswordSettings(null, settings);   // null = inputCheckPasswordEmpty (no current password)
        }

        /// <summary>Changes the password (proves the current one via SRP, sets the new verifier).</summary>
        public async Task ChangePasswordAsync(string currentPassword, string newPassword, string hint)
        {
            var pwd = await Client.Account_GetPassword();                  // FRESH srp_B/srp_id
            var currentProof = await WTelegram.Client.InputCheckPassword(pwd, currentPassword);
            var hash = await NewPasswordHashAsync(pwd, newPassword);
            var settings = new Account_PasswordInputSettings
            {
                flags = Account_PasswordInputSettings.Flags.has_new_algo,
                new_algo = pwd.new_algo,
                new_password_hash = hash,
                hint = hint ?? ""
            };
            await Client.Account_UpdatePasswordSettings(currentProof, settings);
        }

        /// <summary>Disables 2FA (proves the current password, then sets an empty/unknown new algo + empty hash).</summary>
        public async Task DisablePasswordAsync(string currentPassword)
        {
            var pwd = await Client.Account_GetPassword();
            var currentProof = await WTelegram.Client.InputCheckPassword(pwd, currentPassword);
            var settings = new Account_PasswordInputSettings
            {
                flags = Account_PasswordInputSettings.Flags.has_new_algo,
                new_algo = null,                 // serializes as passwordKdfAlgoUnknown → remove password
                new_password_hash = new byte[0]
            };
            await Client.Account_UpdatePasswordSettings(currentProof, settings);
        }

        /// <summary>Recovery-email confirmation (v1): confirm with the emailed code / resend / cancel.</summary>
        public Task<bool> ConfirmPasswordEmailAsync(string code) => Client.Account_ConfirmPasswordEmail(code);
        public Task<bool> ResendPasswordEmailAsync() => Client.Account_ResendPasswordEmail();
        public Task<bool> CancelPasswordEmailAsync() => Client.Account_CancelPasswordEmail();

        // ── Active sessions / devices (account.* + auth.*) ──────────────────
        /// <summary>The account's active sessions (account.getAuthorizations).</summary>
        public Task<Account_Authorizations> GetAuthorizationsAsync()
            => Client.Account_GetAuthorizations();

        /// <summary>Terminates one session by its hash (account.resetAuthorization).</summary>
        public Task<bool> ResetAuthorizationAsync(long hash)
            => Client.Account_ResetAuthorization(hash);

        /// <summary>Terminates all sessions EXCEPT the current one (auth.resetAuthorizations).</summary>
        public Task<bool> TerminateOtherSessionsAsync()
            => Client.Auth_ResetAuthorizations();

        /// <summary>Leaves a channel/supergroup (`channels.leaveChannel`) or a basic group (`messages.deleteChatUser` of self).</summary>
        public Task LeaveChatAsync(InputPeer peer)
        {
            if (peer is InputPeerChannel ch)
                return Client.Channels_LeaveChannel(new InputChannel(ch.channel_id, ch.access_hash));
            if (peer is InputPeerChat pc)
                return Client.Messages_DeleteChatUser(pc.chat_id, new InputUserSelf());
            return Task.CompletedTask;
        }

        /// <summary>Marks a dialog as unread (unread=true) or clears the unread mark (unread=false).</summary>
        public Task<bool> MarkDialogUnreadAsync(InputPeer peer, bool unread)
            => Client.Messages_MarkDialogUnread(new InputDialogPeer { peer = peer }, null, unread);

        /// <summary>Clears a chat's history (keeps the dialog; just_clear). max_id 0 = the whole history.</summary>
        public Task<Messages_AffectedHistory> ClearHistoryAsync(InputPeer peer)
            => Client.Messages_DeleteHistory(peer, 0, just_clear: true);

        /// <summary>Deletes a 1:1 chat's history + dialog (revoke=true also deletes for the other user).</summary>
        public Task<Messages_AffectedHistory> DeleteChatAsync(InputPeer peer, bool revoke)
            => Client.Messages_DeleteHistory(peer, 0, just_clear: false, revoke: revoke);

        /// <summary>The user's chat folders (dialog filters); empty if none/unsupported.</summary>
        public async Task<DialogFilterBase[]> GetDialogFiltersAsync()
        {
            try { var r = await Client.Messages_GetDialogFilters(); return (r as Messages_DialogFilters)?.filters ?? new DialogFilterBase[0]; }
            catch { return new DialogFilterBase[0]; }
        }

        /// <summary>Adds or replaces our reaction on a message (empty emoticon clears it).</summary>
        public async Task<bool> SendReactionAsync(InputPeer peer, int msgId, string emoticon)
        {
            try
            {
                Reaction[] reaction = string.IsNullOrEmpty(emoticon)
                    ? null
                    : new Reaction[] { new ReactionEmoji { emoticon = emoticon } };
                await Client.Messages_SendReaction(peer, msgId, reaction);
                return true;
            }
            catch { return false; }
        }

        // ── Polls & inline-keyboard bot buttons ─────────────────────────────

        /// <summary>Votes in a poll with the chosen option strings (PollAnswer.option). Empty array = retract.
        /// Returns the server Updates (carries the fresh UpdateMessagePoll) so the caller refreshes at once.</summary>
        public Task<UpdatesBase> SendVoteAsync(InputPeer peer, int msgId, string[] options)
            => Client.Messages_SendVote(peer, msgId, options ?? new string[0]);

        /// <summary>The voter list for one public-poll option (paged via next_offset).</summary>
        public Task<Messages_VotesList> GetPollVotesAsync(InputPeer peer, int msgId, string option, string offset, int limit = 50)
            => Client.Messages_GetPollVotes(peer, msgId, limit, option, offset);

        /// <summary>Presses an inline callback button → the bot's answer (toast / alert / url).</summary>
        public Task<Messages_BotCallbackAnswer> GetCallbackAnswerAsync(InputPeer peer, int msgId, byte[] data)
            => Client.Messages_GetBotCallbackAnswer(peer, msgId, data, null, false);

        /// <summary>The /start deep-link handshake — sends "/start &lt;param&gt;" to the bot (Messages_StartBot).</summary>
        public Task<UpdatesBase> StartBotAsync(User bot, string startParam)
            => Client.Messages_StartBot(new InputUser(bot.id, bot.access_hash), bot.ToInputPeer(), RandomId(), startParam ?? "");

        /// <summary>Sends a contact card (used for a reply-keyboard "share phone" request button).</summary>
        public Task<Message> SendContactAsync(InputPeer peer, string phone, string firstName, string lastName)
            => Client.SendMessageAsync(peer, "", new InputMediaContact
            {
                phone_number = phone ?? "", first_name = firstName ?? "", last_name = lastName ?? "", vcard = ""
            });

        private static readonly Random _rng = new Random();
        private static long RandomId() { var b = new byte[8]; _rng.NextBytes(b); return BitConverter.ToInt64(b, 0); }

        /// <summary>Creates and sends a poll (regular / anonymous / multiple-choice). Quiz creation is NOT
        /// offered here — its correct_answers↔option wire encoding is unverified in this build environment
        /// (see batch report); received quizzes still render via PollResults flags.</summary>
        public Task<Message> SendPollAsync(InputPeer peer, string question, string[] options, bool anonymous, bool multiple)
        {
            var answers = new PollAnswer[options.Length];
            for (int i = 0; i < options.Length; i++)
                answers[i] = new PollAnswer
                {
                    text = new TextWithEntities { text = options[i] ?? "" },
                    option = new string((char)i, 1)   // distinct per-answer id (non-quiz: only uniqueness matters)
                };
            var poll = new Poll
            {
                question = new TextWithEntities { text = question ?? "" },
                answers = answers
            };
            if (!anonymous) poll.flags |= Poll.Flags.public_voters;
            if (multiple) poll.flags |= Poll.Flags.multiple_choice;
            return Client.SendMessageAsync(peer, "", new InputMediaPoll { poll = poll });
        }

        /// <summary>Stickers suggested for an emoji (Telegram's "type an emoji to find stickers").</summary>
        public async Task<Document[]> SearchStickersAsync(string emoticon)
        {
            try { var r = await Client.Messages_GetStickers(emoticon); return (r as Messages_Stickers)?.stickers.OfType<Document>().ToArray() ?? new Document[0]; }
            catch { return new Document[0]; }
        }

        /// <summary>The sticker documents in a set.</summary>
        public async Task<Document[]> GetStickerSetAsync(long id, long accessHash)
        {
            try
            {
                var r = await Client.Messages_GetStickerSet(new InputStickerSetID { id = id, access_hash = accessHash });
                return (r as Messages_StickerSet)?.documents.OfType<Document>().ToArray() ?? new Document[0];
            }
            catch { return new Document[0]; }
        }

        /// <summary>The user's saved GIFs (cache-first).</summary>
        public Task<Document[]> GetSavedGifsAsync() => CachedMeta(_savedGifsCache, "gifs", FetchSavedGifs, EmptyDocs);
        private async Task<Document[]> FetchSavedGifs()
        { var r = await Client.Messages_GetSavedGifs(); return (r as Messages_SavedGifs)?.gifs.OfType<Document>().ToArray() ?? EmptyDocs; }

        /// <summary>Sends an existing document (sticker / GIF) to a peer; returns the sent message.</summary>
        public Task<Message> SendDocumentAsync(InputPeer peer, Document doc)
        {
            var media = new InputMediaDocument { id = new InputDocument { id = doc.id, access_hash = doc.access_hash, file_reference = doc.file_reference } };
            return Client.SendMessageAsync(peer, "", media);
        }

        /// <summary>
        /// Sends a plain-text message to a peer; returns the sent message. When
        /// <paramref name="replyToMsgId"/> is &gt; 0 the message is sent as a reply.
        /// </summary>
        public Task<Message> SendTextAsync(InputPeer peer, string text, int replyToMsgId = 0)
        {
            return replyToMsgId > 0
                ? Client.SendMessageAsync(peer, text, reply_to_msg_id: replyToMsgId)
                : Client.SendMessageAsync(peer, text);
        }

        /// <summary>
        /// Deletes messages by id. Uses the channel-specific RPC for channels/supergroups
        /// and the generic one (with <paramref name="revoke"/> = delete for everyone) elsewhere.
        /// </summary>
        public Task DeleteMessagesAsync(InputPeer peer, int[] ids, bool revoke = true)
        {
            if (peer is InputPeerChannel ch)
                return Client.Channels_DeleteMessages(new InputChannel(ch.channel_id, ch.access_hash), ids);
            return Client.Messages_DeleteMessages(ids, revoke);
        }

        // ── Sponsored messages (Telegram ads — required for API compliance) ──
        /// <summary>Fetches sponsored messages for a peer (channel/bot). Cache the result ~5 min.</summary>
        public Task<Messages_SponsoredMessages> GetSponsoredMessagesAsync(InputPeer peer)
        {
            return Client.Messages_GetSponsoredMessages(peer);
        }

        /// <summary>Reports that a sponsored message's full text was shown on screen.</summary>
        public Task ViewSponsoredAsync(byte[] randomId)
        {
            return Client.Messages_ViewSponsoredMessage(randomId);
        }

        /// <summary>Reports a click on a sponsored message (media=true when the click was on its media).</summary>
        public Task ClickSponsoredAsync(byte[] randomId, bool media = false, bool fullscreen = false)
            => Client.Messages_ClickSponsoredMessage(randomId, media, fullscreen);

        /// <summary>Reports a sponsored message. option="" first → ChooseOption; then re-call with the
        /// chosen option to confirm. Returns the channels.SponsoredMessageReportResult variant.</summary>
        public Task<Channels_SponsoredMessageReportResult> ReportSponsoredAsync(byte[] randomId, string option)
            => Client.Messages_ReportSponsoredMessage(randomId, option ?? "");

        /// <summary>Pins or unpins a chat in the dialog list.</summary>
        public Task<bool> ToggleDialogPinAsync(InputPeer peer, bool pinned)
        {
            return Client.Messages_ToggleDialogPin(new InputDialogPeer { peer = peer }, pinned);
        }

        /// <summary>Edits the text of one of my messages.</summary>
        public Task<UpdatesBase> EditMessageAsync(InputPeer peer, int id, string text)
        {
            return Client.Messages_EditMessage(peer, id, message: text);
        }

        /// <summary>Updates my own profile (first/last name + bio).</summary>
        public Task<UserBase> UpdateProfileAsync(string firstName, string lastName, string about)
        {
            return Client.Account_UpdateProfile(firstName, lastName, about);
        }

        /// <summary>My own bio/about text (empty on failure).</summary>
        public async Task<string> GetSelfAboutAsync()
        {
            try
            {
                var full = await Client.Users_GetFullUser(new InputUserSelf());
                return full?.full_user?.about ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Fetches a peer's bio/about (the FULL description) + member count + online count — off the
        /// UI thread and time-bounded (the GetFull* RPC abandons on a VPN black-hole rather than hanging);
        /// empty/zero on timeout or failure. PRESENCE: online = ChannelFull.online_count for megagroups, or
        /// counted from participant statuses for basic groups (broadcasts report 0 — never shown).</summary>
        public async Task<(string about, int members, int online, System.Collections.Generic.List<User> groupUsers)> GetPeerDetailsAsync(InputPeer peer, IPeerInfo info, int timeoutMs = 15000)
        {
            var client = Client;
            if (client == null) return ("", 0, 0, null);
            Func<Task<(string, int, int, System.Collections.Generic.List<User>)>> op;
            if (info is User u)
                op = async () => { var full = await client.Users_GetFullUser(new InputUser(u.id, u.access_hash)); return (full?.full_user?.about ?? "", 0, 0, (System.Collections.Generic.List<User>)null); };
            else if (peer is InputPeerChannel ch)
                op = async () => { var full = await client.Channels_GetFullChannel(new InputChannel(ch.channel_id, ch.access_hash)); var cf = full?.full_chat as ChannelFull; return (cf?.about ?? "", cf?.participants_count ?? 0, cf?.online_count ?? 0, (System.Collections.Generic.List<User>)null); };
            else if (peer is InputPeerChat pc)
                op = async () =>
                {
                    var full = await client.Messages_GetFullChat(pc.chat_id);
                    var cf = full?.full_chat as ChatFull;
                    var plist = (cf?.participants as ChatParticipants)?.participants;
                    int members = plist?.Length ?? 0;
                    int online = 0;
                    var users = new System.Collections.Generic.List<User>();
                    if (full?.users != null)
                    {
                        var utc = DateTime.UtcNow;
                        foreach (var ub in full.users.Values)
                            if (ub is User pu && pu.status is UserStatusOnline on && on.expires > utc) online++;
                        // PROFILE-MEMBERS: basic-group member preview rides this SAME fetch (participant order).
                        if (plist != null)
                            foreach (var p in plist)
                                if (full.users.TryGetValue(p.UserId, out var mb) && mb is User mu) users.Add(mu);
                    }
                    return (cf?.about ?? "", members, online, users);
                };
            else return ("", 0, 0, null);
            try
            {
                var task = Task.Run(op);
                var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (done != task) { SwallowTaskFault(task); System.Diagnostics.Debug.WriteLine("[PROFILE] about fetch TIMED OUT"); return ("", 0, 0, null); }
                return await task.ConfigureAwait(false);
            }
            catch { return ("", 0, 0, null); }
        }

        /// <summary>PRESENCE 1.1: account.updateStatus — tells Telegram we're online (offline=false) or
        /// away (offline=true). Caller owns throttling/idle policy; this is the bare bounded RPC.
        /// Returns the server's Bool so callers can log a rejected send (false = not accepted).</summary>
        public Task<bool> UpdateStatusAsync(bool offline)
        {
            var client = Client;
            if (client == null || TearingDown) return Task.FromResult(false);
            return client.Account_UpdateStatus(offline);
        }

        /// <summary>Forwards messages (by id) from one peer to another.</summary>
        public Task<UpdatesBase> ForwardMessagesAsync(InputPeer fromPeer, int[] ids, InputPeer toPeer)
        {
            // Each forwarded message needs a unique client random_id.
            var rnd = new long[ids.Length];
            var rng = new Random();
            for (int i = 0; i < ids.Length; i++)
                rnd[i] = ((long)rng.Next(int.MinValue, int.MaxValue) << 32) ^ (uint)rng.Next();
            return Client.Messages_ForwardMessages(fromPeer, ids, rnd, toPeer);
        }

        /// <summary>Forwards to a SINGLE target, off the UI thread + time-bounded (for the multi-target picker
        /// loop — one slow/black-holed target must not hang the others). Returns the RPC's UpdatesBase (so the
        /// caller can route it through the single chat-list refresh), or null on timeout/error.</summary>
        public async Task<UpdatesBase> ForwardToAsync(InputPeer fromPeer, int[] ids, InputPeer toPeer, int timeoutMs = 20000)
        {
            var client = Client;
            if (client == null || ids == null || ids.Length == 0 || toPeer == null) return null;
            try
            {
                var task = Task.Run(() => ForwardMessagesAsync(fromPeer, ids, toPeer));
                var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (done != task) { SwallowTaskFault(task); System.Diagnostics.Debug.WriteLine("[FWD] target TIMED OUT"); return null; }
                return await task.ConfigureAwait(false);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[FWD] error: " + ex.Message); return null; }
        }

        /// <summary>
        /// Downloads the largest size of a photo; returns its bytes and the on-disk
        /// cache path it was persisted to (null if the cache write failed).
        /// </summary>
        public async Task<(byte[] bytes, string cachePath)> DownloadPhotoAsync(Photo photo)
        {
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await Client.DownloadFileAsync(photo, ms, photo.LargestPhotoSize);
                bytes = ms.ToArray();
            }

            string cachePath = null;
            string p = MediaCache.MediaPath("photo_" + photo.id + ".jpg");   // full photo → media/ (on-demand; ensures media/)
            try
            {
                // Temp-then-rename (DOWNLOAD-FIX): a crash mid-WriteAllBytes must not strand a partial JPEG at
                // the final path where the exists-check would accept it forever.
                File.WriteAllBytes(p + ".part", bytes);
                if (File.Exists(p)) File.Delete(p);
                File.Move(p + ".part", p);
                cachePath = p;
                if (TelegArm.Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[CACHE] photo media disk OK → " + p + " (" + bytes.Length + "b)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[CACHE] photo media disk FAILED → " + p + " : " + ex.GetType().FullName + ": " + ex.Message);
                cachePath = null; // cache is best-effort; bytes are still returned
            }

            return (bytes, cachePath);
        }

        /// <summary>Disconnects and disposes the underlying client.</summary>
        public void Dispose()
        {
            TearingDown = true;   // before dispose — observers classify the race as expected (1.2)
            System.Diagnostics.Debug.WriteLine("[CONN] client disposed reason=app-close");
            Client?.Dispose();
            Client = null;
        }

        /// <summary>Tears the connection down for an account SWITCH — KEEPS the session (just disconnects):
        /// stop the watchdog, persist the update state, dispose the client, and null the live handles so a
        /// fresh client can be built on the target account. Distinct from Auth_LogOut (which removes the account).</summary>
        public async System.Threading.Tasks.Task TeardownForSwitchAsync(int timeoutMs = 10000)
        {
            TearingDown = true;   // before anything lets go of the client (TEARDOWN-HYGIENE 1.2)
            System.Diagnostics.Debug.WriteLine("[CONN] client disposed reason=acct-switch");
            StopConnectionWatchdog();
            try { if (Updates != null) Updates.SaveState(UpdateStatePath); } catch { }

            var client = Client;
            Client = null; Updates = null; Me = null;     // detach FIRST so nothing touches the client mid-teardown
            _silentResume = false; NeedsInteractiveLogin = false; _contactsCache = null;
            ClearPickerMetaCache();   // per-account picker metadata → the new account re-fetches its own

            if (client != null)
            {
                // Abort the socket FIRST (so Dispose has no network to wait on → it can't hang), THEN Dispose —
                // which FLUSHES the session and RELEASES the file handle. We AWAIT the dispose to COMPLETION
                // (generous bound) so the session is written cleanly and the lock is gone BEFORE any caller moves/
                // deletes/reopens that file. Abandoning the dispose mid-write was the corruption ("buffer is null")
                // + the leftover handle ("being used by another process") — so the bound is now a last-resort
                // freeze guard, not the normal path. A healthy-client teardown (switch) completes in well under 1s.
                var teardown = System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await client.ResetAsync(false, false); } catch { }
                    try { client.Dispose(); } catch { }
                });
                var done = await System.Threading.Tasks.Task.WhenAny(teardown, System.Threading.Tasks.Task.Delay(timeoutMs));
                if (done == teardown) System.Diagnostics.Debug.WriteLine("[ACCT] teardown complete (session flushed, handle released)");
                else { System.Diagnostics.Debug.WriteLine("[ACCT] WARNING: teardown timed out (" + timeoutMs + "ms) — session handle may linger briefly"); SwallowTaskFault(teardown); }
            }
        }

        /// <summary>Discards the CURRENT client (dispose + null) and waits for its session-file handle to release,
        /// so the next connect attempt opens a FRESH client on a FREE file. Used between failed connect attempts —
        /// the old code reused a faulted client (EnsureClient: `if (Client != null) return`), which looped on
        /// "config value for phone_number" / kept the file "in use".</summary>
        public async System.Threading.Tasks.Task DiscardFaultedClientAsync()
        {
            TearingDown = true;
            System.Diagnostics.Debug.WriteLine("[CONN] client disposed reason=rebuild");
            var client = Client;
            Client = null; Updates = null; Me = null;
            if (client == null) return;
            var t = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await client.ResetAsync(false, false); } catch { }
                try { client.Dispose(); } catch { }
            });
            var done = await System.Threading.Tasks.Task.WhenAny(t, System.Threading.Tasks.Task.Delay(8000));
            if (done != t) SwallowTaskFault(t);
        }

        /// <summary>INSTANT logout: detaches the client immediately (the UI transitions right away), then does
        /// ALL the slow work — best-effort server auth.logOut, hard disconnect, dispose, and delete
        /// accounts/{id}/ + Cache/{id}/ — entirely in the BACKGROUND (fire-and-forget, each step time-bounded).
        /// The UI never waits on the network, so a black-holed VPN can't make logout look frozen.</summary>
        public void BeginLogoutCleanup(long id)
        {
            TearingDown = true;
            System.Diagnostics.Debug.WriteLine("[CONN] client disposed reason=logout");
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] BeginLogoutCleanup: before StopConnectionWatchdog");
            StopConnectionWatchdog();
            // Deliberately SKIP Updates.SaveState — the account (and its update-state file) is being DELETED, and
            // SaveState can BLOCK on an internal UpdateManager lock held by the update loop stuck on the dead VPN.
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] BeginLogoutCleanup: after StopWatchdog (skipped SaveState); before null handles");
            var client = Client;
            Client = null; Updates = null; Me = null;
            _silentResume = false; NeedsInteractiveLogin = false;
            // Mark this account "deleting" NOW (synchronously) so a concurrent switch/corrupt-recovery skips it as
            // a candidate — it must never try to resume an account whose files the background cleanup is removing.
            AccountStore.MarkDeleting(id);
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] BeginLogoutCleanup: after null handles; firing background cleanup");

            var ignore = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (client != null)
                    {
                        // Bound the WHOLE client teardown so a hung dispose can't block the folder delete.
                        var teardown = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try { var lo = client.Auth_LogOut(); await System.Threading.Tasks.Task.WhenAny(lo, System.Threading.Tasks.Task.Delay(3000)); } catch { }
                            try { var rs = client.ResetAsync(false, false); await System.Threading.Tasks.Task.WhenAny(rs, System.Threading.Tasks.Task.Delay(2000)); } catch { }
                            try { client.Dispose(); } catch { }   // socket aborted by Reset → releases the session-file lock
                        });
                        await System.Threading.Tasks.Task.WhenAny(teardown, System.Threading.Tasks.Task.Delay(8000));
                    }
                    await AccountStore.DeleteAccountAsync(id);   // lock released → deletes cleanly (bounded retry covers a lingering handle)
                    AccountStore.ClearPending();
                    System.Diagnostics.Debug.WriteLine("[ACCT] background logout cleanup done id=" + id);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ACCT] bg logout cleanup error: " + ex.Message); }
                finally { AccountStore.UnmarkDeleting(id); }
            });
        }

        /// <summary>BEST-EFFORT server logout (auth.logOut): off the UI thread + time-bounded. Used by the
        /// add-account dedup path to revoke a duplicate pending session.</summary>
        public async System.Threading.Tasks.Task LogOutServerAsync(int timeoutMs = 6000)
        {
            var client = Client;
            if (client == null) return;
            try
            {
                // Task.Run: Auth_LogOut may BLOCK synchronously establishing the link before it yields — keep
                // that off the UI thread.
                var logoutTask = System.Threading.Tasks.Task.Run(() => client.Auth_LogOut());
                var done = await System.Threading.Tasks.Task.WhenAny(logoutTask, System.Threading.Tasks.Task.Delay(timeoutMs));
                if (done == logoutTask)
                {
                    try { await logoutTask; System.Diagnostics.Debug.WriteLine("[ACCT] Auth_LogOut done (server session invalidated)"); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ACCT] Auth_LogOut threw: " + ex.Message); }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[ACCT] Auth_LogOut TIMED OUT (" + timeoutMs + "ms) → proceeding to local cleanup");
                    _ = logoutTask.ContinueWith(t => { var ignore = t.Exception; },   // it faults when the client is disposed — observe it
                        System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ACCT] LogOutServerAsync error: " + ex.Message); }
        }
    }
}
