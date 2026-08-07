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

        /// <summary>MULTI-ACCOUNT (increment 3b): the account THIS service is bound to. 0 = the original startup
        /// service → follow the static active/legacy resolution (behavior-neutral). A warm/per-account service sets
        /// its own id so session/updates/phone resolve to ITS accounts/{id}/ dir, NOT the global static ActiveId —
        /// so two services never write each other's files.</summary>
        public long AccountId { get; set; }

        /// <summary>Session file for THIS service's account (per-instance when AccountId set; else the static active
        /// path — identical for the single startup service). Read by Config("session_pathname").</summary>
        public string SessionPath => AccountId != 0 && !AccountContext.LegacyMode
            ? System.IO.Path.Combine(AccountContext.AccountDir(AccountId), "session") : AccountContext.SessionPath;

        /// <summary>Stored phone for THIS service's account (for silent resume's Config("phone_number")).</summary>
        public string PhonePath => AccountId != 0 && !AccountContext.LegacyMode
            ? System.IO.Path.Combine(AccountContext.AccountDir(AccountId), "phone") : AccountContext.PhonePath;

        /// <summary>UpdateManager state file for THIS service's account.</summary>
        public string UpdateStatePath => AccountId != 0 && !AccountContext.LegacyMode
            ? System.IO.Path.Combine(AccountContext.AccountDir(AccountId), "updates") : AccountContext.UpdatePath;

        /// <summary>True when there's any account to resume (a multi-account dir or a legacy session).</summary>
        public static bool SessionExists => AccountStore.HasAnyAccountOrLegacy();

        private bool _silentResume;

        public WTelegram.Client Client { get; private set; }
        public User Me { get; private set; }

        /// <summary>True once the client holds a logged-in user (session is live).</summary>
        public bool IsAuthorized => Client?.User != null;

        /// <summary>MULTI-ACCOUNT (per-instance AvatarStore): each service owns its avatar store — mem cache + disk,
        /// downloading via ITS OWN client — so two accounts never cross-serve avatars. MainForm reads the ACTIVE
        /// service's store (via <see cref="AvatarStore.Current"/>). Created once with the service; its disk root still
        /// follows the account currently shown (CacheRootFor(ActiveId)), which is correct since only the active store
        /// is exercised by the UI.</summary>
        public AvatarStore Avatars { get; }

        public TelegramService()
        {
            Avatars = new AvatarStore(p => DownloadAvatarAsync(p));
        }

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
            if (what == "session_pathname")
            {
                var __sp = SessionPath;   // the ACTUAL file this (active) client opens — AccountId-resolved
                // [SESSPATH] 0.2 collision probe: two active/warm clients logging the SAME session= path = the bug.
                // Note AccountId==0 falls back to the GLOBAL AccountContext.SessionPath (keyed off ActiveId) — the
                // prime collision suspect; the globalActiveId field makes that visible.
                if (TelegArm.Helpers.Logger.Enabled)
                    TelegArm.Helpers.Logger.Diag("[SESSPATH] client-open acct=" + AccountId + " session=\"" + __sp
                        + "\" globalActiveId=" + AccountContext.ActiveId + " legacy=" + AccountContext.LegacyMode);
                return __sp;
            }
            // Device identity reported to Telegram (the session-list strings). Without these, WTelegram's
            // defaults produced the bogus "BlackBerry · Windows 10" — supply honest values instead. The app
            // NAME shown next to app_version comes from the api_id registration ("TelegArm"), so a release
            // session reads "TelegArm 1.1.0"; device_model is the secondary device line (the user's PC name,
            // as official clients show, falling back to "Desktop").
            if (what == "device_model") return string.IsNullOrWhiteSpace(Environment.MachineName) ? "Desktop" : Environment.MachineName;
            if (what == "system_version") return SystemVersion;
            if (what == "app_version") return Program.Version;   // AssemblyInfo 1.1.0.0 → "1.1.0"
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

        private string LoadPhone()
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
            // BATCH-TA-13/L1 — rungs INSIDE the single largest unattributed interval in startup.
            // The device measured 5,010 ms (45.4% of an 11,037 ms cold start) between MainForm.OnLoad and
            // "LoginAsync returned AUTHORIZED", with NOT ONE rung inside it — the exact shape TA-9 Part B
            // warned gets misattributed to whatever rung happens to follow. It matters more now that TA-12
            // established a FLOOD_WAIT of up to 60 s is absorbed as a silent Task.Delay inside an awaited
            // WTC call: a rate-limit sleep would be hiding in precisely this interval and would look like a
            // hang. These rungs bound the phone read and the client construction; the split INSIDE
            // LoginUserIfNeeded (TCP → handshake → auth) now comes from W1's [WTC] lines, which is strictly
            // better than anything we could stamp from outside because it is the library's own view.
            // NOTE: PerfLog.Boot emits for the life of the process, so these also instrument ACCOUNT SWITCHES
            // and reconnects, not just cold start — deliberate, and cheap (Logger.Diag is Enabled-gated).
            PerfLog.Boot("  LoginAsync ENTER (silentResume=" + silentResume + ")");
            _silentResume = silentResume;
            NeedsInteractiveLogin = false;

            // ALWAYS reload the active account's stored phone for a silent resume — NOT just when empty. A
            // prior interactive attempt (e.g. an abandoned add-account) leaves a STALE number in AuthManager;
            // reusing it when switching back to another account makes that account's resume need an
            // interactive login → a spurious "all accounts logged out" (the MULTI-fix wipe).
            if (silentResume)
                AuthManager.PhoneNumber = LoadPhone();
            PerfLog.Boot("  LoginAsync: phone loaded (disk)");

            EnsureClient();
            PerfLog.Boot("  LoginAsync: EnsureClient done (client constructed, session file opened)");

            Me = await Client.LoginUserIfNeeded();
            PerfLog.Boot("  LoginAsync: LoginUserIfNeeded RETURNED (TCP + MTProto handshake + auth)");

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
            ApplyProxyTo(Client, "EnsureClient");
            TearingDown = false;   // a fresh client ends the teardown window (TEARDOWN-HYGIENE 1.2)
        }

        /// <summary>BATCH-TA-16 — point a freshly-constructed client at the configured MTProxy, if any.
        /// MTProxyUrl is a plain string property read ONLY inside WTC's DoConnectAsync (src/Client.cs:880),
        /// so setting it at construction is enough for every client we create; changing it on a LIVE client
        /// does nothing until that client reconnects. Null = connect directly, exactly as before.
        /// WTC's clone ctor copies MTProxyUrl (src/Client.cs:141), so per-DC clients inherit it — which is
        /// what makes media/file downloads on a secondary DC go through the proxy too.
        /// ⚠ LOGS host:port ONLY, never the secret — see ProxyUrl's remarks.</summary>
        private static void ApplyProxyTo(WTelegram.Client c, string why)
        {
            if (c == null) return;
            string url = null;
            try { url = AppSettings.Instance.ActiveProxyUrl; } catch { }
            c.MTProxyUrl = url;                       // null is meaningful: it means "direct"
            if (TelegArm.Helpers.Logger.Enabled)
                TelegArm.Helpers.Logger.Diag("[PROXY] " + why + " → " + (url == null ? "DIRECT (no proxy)"
                                                                                     : "via " + ProxyUrl.SafeForLog(url)));
        }

        /// <summary>BATCH-TA-17 — apply a CHANGED proxy (or a switch back to direct) to THIS live client,
        /// immediately.
        ///
        /// WHY A RECONNECT IS UNAVOIDABLE: WTC reads MTProxyUrl ONLY inside DoConnectAsync
        /// (src/Client.cs:880). Assigning it to a connected client changes nothing — which is exactly the
        /// bug this fixes: picking a different proxy, or turning the proxy off, left the app happily
        /// running on the OLD transport, so a proxy the user had just seen fail kept "working" and a
        /// switch to direct silently didn't happen.
        ///
        /// It reuses the EXISTING ForceReconnectAsync rather than inventing a reconnect: that path already
        /// does ResetAsync(false,false) — which keeps the user and the session — then ConnectAsync, and it
        /// is the same path the liveness watchdog uses, so it is the one that has been exercised. Its
        /// re-entrancy guard means a reconnect already in flight simply wins.
        /// ⚠ THE WARM POOL IS THE CALLER'S PROBLEM, not this method's: warm clients were built with the
        ///   OLD transport and must be torn down and re-warmed by MainForm (see ApplyProxyChangeAsync
        ///   there). This method only owns the ACTIVE client.</summary>
        public async Task ApplyProxyChangeAsync()
        {
            ApplyProxyTo(Client, "apply-live");   // takes effect on the reconnect below, not before
            await ForceReconnectAsync().ConfigureAwait(false);
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
        /// <summary>INCREMENT 3b (tier-1 seamless switch): the dialog list captured at seed time, so a rebind to this
        /// (warm) service can paint the chat list INSTANTLY from memory — no network round-trip, no blank reload — while
        /// a live refresh catches up in the background. May be slightly stale (messages that arrived while warm were
        /// silenced by the router); the refresh reconciles it.</summary>
        public Messages_DialogsBase CachedDialogs { get; private set; }

        public async Task SeedUpdateManagerAsync()
        {
            var mgr = Updates;
            if (mgr == null || Client == null) return;
            try
            {
                System.Diagnostics.Debug.WriteLine("[UM] seeding: Messages_GetAllDialogs → LoadDialogs…");
                var dialogs = await Client.Messages_GetAllDialogs();
                CachedDialogs = dialogs;                 // snapshot for an instant tier-1 rebind render
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

        // AsInputChannel removed (WIZOU-REVIEW): WTC provides an implicit Channel→InputChannel conversion, so a
        // Channel is passed directly to Channels_* methods. AsInputPeerUser stays — WTC 4.4.6 has NO implicit
        // User→InputPeer conversion, so the explicit InputPeerUser construction is still required.
        private static InputPeerUser AsInputPeerUser(User u) { return new InputPeerUser(u.id, u.access_hash); }

        // TIER 1 — edit info + members
        public Task<bool> EditChatTitleAsync(Channel ch, string title, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Channels_EditTitle(ch, title ?? ""); return true; }, timeoutMs, "EditTitle");
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
                await Client.Channels_EditPhoto(ch,
                    new InputChatUploadedPhoto { file = file, flags = InputChatUploadedPhoto.Flags.has_file });
                await RefreshChannelPhotoAsync(ch);   // CHANNEL-PHOTO-REFRESH: update ch.photo to the NEW one so a re-download fetches it (not the stale id)
                return true;
            }, timeoutMs, "EditPhoto");
        }

        /// <summary>CHANNEL-PHOTO-REFRESH: re-fetch the channel to update its <c>.photo</c> to the freshly-set one.
        /// The avatar download builds its file location from the peer's photo_id, so without this a re-fetch after a
        /// photo change would re-download the OLD photo by its stale id. Mutates the SHARED Channel (== ChatEntry.PeerInfo).</summary>
        private async Task RefreshChannelPhotoAsync(Channel ch)
        {
            try
            {
                var res = await Client.Channels_GetChannels(new InputChannelBase[] { (InputChannel)ch }).ConfigureAwait(false);
                if (res?.chats != null && res.chats.TryGetValue(ch.id, out var cb) && cb is Channel fresh)
                    ch.photo = fresh.photo;
            }
            catch { }
        }

        public Task<Channels_ChannelParticipants> GetParticipantsAsync(Channel ch, ChannelParticipantsFilter filter, int offset, int limit, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () =>
                await Client.Channels_GetParticipants(ch, filter, offset, limit, 0) as Channels_ChannelParticipants,
                timeoutMs, "GetParticipants");
        }

        /// <summary>Remove a member: ban (view_messages) then immediately unban → kicked but may rejoin.</summary>
        public Task<bool> KickMemberAsync(Channel ch, User user, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () =>
            {
                var peer = AsInputPeerUser(user);
                await Client.Channels_EditBanned(ch, peer, new ChatBannedRights { flags = ChatBannedRights.Flags.view_messages });
                await Client.Channels_EditBanned(ch, peer, new ChatBannedRights { flags = 0 });
                return true;
            }, timeoutMs, "Kick");
        }

        // TIER 2 — admins / permissions / bans
        public Task<bool> SetAdminAsync(Channel ch, User user, ChatAdminRights rights, string rank, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Channels_EditAdmin(ch, new InputUser(user.id, user.access_hash), rights, rank ?? ""); return true; }, timeoutMs, "EditAdmin");
        }

        public Task<bool> SetDefaultPermissionsAsync(InputPeer peer, ChatBannedRights rights, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Messages_EditChatDefaultBannedRights(peer, rights); return true; }, timeoutMs, "DefaultPerms");
        }

        public Task<bool> BanMemberAsync(Channel ch, User user, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Channels_EditBanned(ch, AsInputPeerUser(user), new ChatBannedRights { flags = ChatBannedRights.Flags.view_messages }); return true; }, timeoutMs, "Ban");
        }

        public Task<bool> UnbanMemberAsync(Channel ch, User user, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Channels_EditBanned(ch, AsInputPeerUser(user), new ChatBannedRights { flags = 0 }); return true; }, timeoutMs, "Unban");
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
            return AdminBoundedAsync(() => Client.Channels_CheckUsername(ch, username ?? ""), timeoutMs, "CheckUsername");
        }

        public Task<bool> UpdateUsernameAsync(Channel ch, string username, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(() => Client.Channels_UpdateUsername(ch, username ?? ""), timeoutMs, "UpdateUsername");
        }

        public Task<bool> ToggleSignaturesAsync(Channel ch, bool on, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () => { await Client.Channels_ToggleSignatures(ch, on, false); return true; }, timeoutMs, "ToggleSignatures");
        }

        // ── CHANNEL-LINK-UNLINK: a broadcast channel's discussion group (the admin side of comments) ──────────
        /// <summary>The channel's FULL info — read <c>(.full_chat as ChannelFull).linked_chat_id</c> for the current
        /// discussion group (0 = none / comments off); <c>.chats</c> resolves the linked group's name. Bounded.</summary>
        public Task<Messages_ChatFull> GetChannelFullAsync(Channel ch, int timeoutMs = 15000)
        {
            return AdminBoundedAsync(() => Client.Channels_GetFullChannel(ch), timeoutMs, "GetFullChannel");
        }

        /// <summary>Groups eligible to link as a broadcast channel's discussion group (no Client.* wrapper → Invoke).</summary>
        public Task<Messages_Chats> GetGroupsForDiscussionAsync(int timeoutMs = 20000)
        {
            return AdminBoundedAsync(() => Client.Invoke(new TL.Methods.Channels_GetGroupsForDiscussion()), timeoutMs, "GetGroupsForDiscussion");
        }

        /// <summary>Links <paramref name="group"/> to <paramref name="broadcast"/> (comments ON), or UNLINKS when
        /// <paramref name="group"/> is null — WTC serializes a null InputChannel as inputChannelEmpty (comments OFF).</summary>
        public Task<bool> SetDiscussionGroupAsync(Channel broadcast, Channel group, int timeoutMs = 20000)
        {
            InputChannel b = broadcast;                                        // implicit Channel → InputChannel
            InputChannelBase g = group == null ? null : (InputChannel)group;  // null → inputChannelEmpty (unlink)
            return AdminBoundedAsync(async () => { await Client.Invoke(new TL.Methods.Channels_SetDiscussionGroup { broadcast = b, group = g }); return true; }, timeoutMs, "SetDiscussionGroup");
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

        /// <summary>Marks "the server is alive": call on every received update and on probe/reconnect success.
        ///
        /// BATCH-TA-16e/E3 — THIS IS ALSO WHERE THE PROXY PILL LEARNS IT IS CONNECTED AGAIN, and it has to
        /// be here rather than in our connect loop. ConnectResilientlyAsync only runs for OUR reconnects;
        /// WTC's reactor re-establishes the socket entirely on its own (src/Client.cs:388-397, a fixed 5 s
        /// retry that only surfaces every MaxAutoReconnects'th time), so on a proxy that drops every few
        /// seconds the pill would latch at "Connecting…" forever with nothing to clear it — exactly the
        /// stuck state reported from the device.
        /// NoteActivity is the ONE place every "the server just answered" path already funnels through:
        /// received updates (MainForm.OnManagerUpdate), a successful liveness probe, and a completed forced
        /// reconnect. Any of those is proof the transport is up, whoever re-established it.
        /// Cheap: ProxyStatus only raises its event when the state actually CHANGES, so the common case
        /// (already Connected, another update arrives) is a lock and two comparisons.</summary>
        public void NoteActivity()
        {
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            ProxyStatus.NoteAuthorized();
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

        // ── WARM CONNECTIONS (ACCOUNT-SWITCH STEP 1): background clients bound to an EXPLICIT account id ──

        /// <summary>A headless, connected background client for a NON-active account — kept alive (NO UpdateManager →
        /// silent + adoptable) so a switch can REUSE its live connection instead of re-handshaking.</summary>
        public sealed class WarmClient
        {
            public long Id;
            public WTelegram.Client Client;
            public User Me;
            public void Dispose() { try { if (Client != null) Client.Dispose(); } catch { } }
        }

        /// <summary>Config for a warm client bound to an EXPLICIT id — session/updates/phone from AccountDir(id), NOT
        /// the static ActiveId. Resume-only: interactive prompts return null (an invalid session aborts, never logs in).</summary>
        private static string WarmConfig(string what, long id)
        {
            switch (what)
            {
                case "api_id": return ApiCredentials.ApiId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "api_hash": return ApiCredentials.ApiHash;
                case "session_pathname":
                {
                    var __wp = System.IO.Path.Combine(AccountContext.AccountDir(id), "session");
                    if (TelegArm.Helpers.Logger.Enabled)
                        TelegArm.Helpers.Logger.Diag("[SESSPATH] warm-open acct=" + id + " session=\"" + __wp + "\"");
                    return __wp;
                }
                case "device_model": return string.IsNullOrWhiteSpace(Environment.MachineName) ? "Desktop" : Environment.MachineName;
                case "system_version": return SystemVersion;
                case "app_version": return Program.Version;
                case "lang_code": return "en";
                case "system_lang_code": return "en";
                case "phone_number":
                    try
                    {
                        var pp = System.IO.Path.Combine(AccountContext.AccountDir(id), "phone");
                        return System.IO.File.Exists(pp) ? Security.Unprotect(System.IO.File.ReadAllText(pp).Trim()) : null;
                    }
                    catch { return null; }
                default: return null;
            }
        }

        /// <summary>STEP 1: spin up a headless background client for <paramref name="id"/> (its own paths) and RESUME
        /// it (no login) — connected + kept alive, NO UpdateManager (silent + adoptable). Null on any failure (invalid/
        /// expired session → the account is simply skipped). Bounded so a hung resume can't stall startup.</summary>
        public static async System.Threading.Tasks.Task<WarmClient> CreateWarmClientAsync(long id, int timeoutMs = 20000)
        {
            WTelegram.Client c = null;
            try
            {
                // ⚠ BUG 3b — GUARD #1: DO NOT OPEN THE SESSION IF THIS ACCOUNT IS ALREADY THE ACTIVE ONE.
                // Warming is started fire-and-forget and can sit queued behind a stagger delay, so by the
                // time this runs the user may have switched INTO this account. Constructing the client is
                // exactly what opens accounts/{id}/session — a second handle on the file the active client
                // already holds, which is the AUTH_KEY_DUPLICATED / mid-write corruption path. Until now
                // this was only detected AFTER the fact (MainForm.WarmOneAsync disposed the loser), which
                // shortened the overlap but never prevented it.
                if (id == AccountContext.ActiveId)
                {
                    if (TelegArm.Helpers.Logger.Enabled)
                        TelegArm.Helpers.Logger.Diag("[WARMCONN] warm ABORTED before open id=" + id + " — it is the ACTIVE account (Bug 3b guard #1)");
                    return null;
                }

                c = new WTelegram.Client(w => WarmConfig(w, id));
                c.MaxAutoReconnects = 1000;
                // A warm client must use the SAME transport as the active one, or switching accounts would
                // silently move the user between proxied and direct. (Changing the proxy while warm clients
                // are already live is the separate, gated problem — BATCH-TA-16/P5.)
                ApplyProxyTo(c, "warm id=" + id);
                var login = c.LoginUserIfNeeded();
                var winner = await System.Threading.Tasks.Task.WhenAny(login, System.Threading.Tasks.Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (winner != login)
                {
                    System.Diagnostics.Debug.WriteLine("[WARMCONN] id=" + id + " resume TIMED OUT — skipping");
                    _ = login.ContinueWith(t => { var e = t.Exception; }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                    try { c.Dispose(); } catch { }
                    return null;
                }
                var me = await login.ConfigureAwait(false);
                if (me == null || c.User == null) { try { c.Dispose(); } catch { } return null; }

                // ⚠ BUG 3b — GUARD #2: the resume above is the LONG await (a full handshake, up to
                // timeoutMs), and a switch INTO this account during it is precisely the race. Re-check
                // before handing the client back, and release the session the SAFE way — socket abort
                // FIRST, then Dispose, AWAITED — so the file handle is gone before the active client
                // opens the same path. A bare c.Dispose() here would be the very pattern
                // DisposeWarmServiceAsync exists to avoid (ACCOUNT-RECOVERY-SAFETY Bug 1).
                if (id == AccountContext.ActiveId)
                {
                    if (TelegArm.Helpers.Logger.Enabled)
                        TelegArm.Helpers.Logger.Diag("[WARMCONN] warm ABORTED after resume id=" + id + " — became the ACTIVE account mid-warm (Bug 3b guard #2)");
                    var drop = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try { await c.ResetAsync(false, false); } catch { }   // abort the socket FIRST
                        try { c.Dispose(); } catch { }                        // then flush + RELEASE the handle
                    });
                    var dropped = await System.Threading.Tasks.Task.WhenAny(drop, System.Threading.Tasks.Task.Delay(10000)).ConfigureAwait(false);
                    if (dropped != drop) SwallowTaskFault(drop);
                    return null;
                }

                System.Diagnostics.Debug.WriteLine("[WARMCONN] id=" + id + " connected user=" + me.id);
                return new WarmClient { Id = id, Client = c, Me = me };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WARMCONN] id=" + id + " failed: " + ex.Message);
                try { if (c != null) c.Dispose(); } catch { }
                return null;
            }
        }

        /// <summary>STEP 1: adopt a pre-connected warm client as this service's live client — SKIPS EnsureClient + the
        /// MTProto reconnect handshake. The caller then runs the normal post-connect (StartUpdateManager attaches the
        /// UM + getDifference on the live connection; LoadDialogs). Authorized iff the adopted client's User is set.</summary>
        public void AdoptConnectedClient(WTelegram.Client client, User me)
        {
            Client = client;
            Me = me;
            if (Client != null) Client.MaxAutoReconnects = 1000;
            TearingDown = false;
            _silentResume = true;
            NeedsInteractiveLogin = false;
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>Bounded liveness ping (Help_GetConfig) to VERIFY an adopted warm client is actually alive before
        /// committing to it (else the switch falls back to a fresh connect).</summary>
        public Task<bool> PingAliveAsync() => ProbeAliveAsync();

        /// <summary>INCREMENT 3b: build a full warm SERVICE for a NON-active account — a resumed background client wrapped
        /// in a TelegramService that owns its AccountId + per-instance AvatarStore + a ROUTED UpdateManager (so it keeps
        /// live pts state silently, and a switch REBINDS to it with no handshake and no UM re-attach). The UM routes
        /// through <paramref name="router"/> keyed by this service's id (dropped while non-active). Null on any failure.</summary>
        public static async System.Threading.Tasks.Task<TelegramService> CreateWarmServiceAsync(long id, Func<TelegramService, Update, System.Threading.Tasks.Task> router, int timeoutMs = 20000)
        {
            var wc = await CreateWarmClientAsync(id, timeoutMs).ConfigureAwait(false);
            if (wc == null) return null;
            var svc = new TelegramService { AccountId = id };
            svc.AdoptConnectedClient(wc.Client, wc.Me);
            try
            {
                svc.StartUpdateManager(u => router(svc, u));                 // routed: keyed by THIS service (its id → active/background)
                await svc.SeedUpdateManagerAsync().ConfigureAwait(false);    // baseline pts → getDifference keeps it current
                await svc.LoadNotifyDefaultsAsync().ConfigureAwait(false);   // NOTIFY-BACKGROUND: its own category mute defaults
                await svc.SeedNotifyExceptionsAsync().ConfigureAwait(false); // TA-26/B3: the per-peer exceptions, one round-trip
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WARMCONN] warm-service id=" + id + " init failed: " + ex.Message);
                try { svc.DisposeWarmService(); } catch { }
                return null;
            }
            System.Diagnostics.Debug.WriteLine("[WARMCONN] warm SERVICE ready id=" + id);
            return svc;
        }

        /// <summary>Dispose a warm (background) service: persist its UM state, dispose its avatar store + watchdog, then
        /// dispose the client. Used on app exit / account removal / a failed warm init.</summary>
        public void DisposeWarmService()
        {
            try { if (Updates != null) Updates.SaveState(UpdateStatePath); } catch { }
            try { if (Avatars != null) Avatars.Dispose(); } catch { }
            try { StopConnectionWatchdog(); } catch { }
            Dispose();
        }

        /// <summary>ACCOUNT-RECOVERY-SAFETY (Bug 1): the SWITCH-SAFE warm teardown. The sync <see cref="DisposeWarmService"/>
        /// calls <see cref="Dispose"/> → Client.Dispose() DIRECTLY — no socket-abort-first, not awaited — so when a switch
        /// drops a warm client and then COLD-opens the SAME session file, the warm client's connection/handle can still be
        /// releasing → two clients on one session = AUTH_KEY_DUPLICATED / mid-write corruption (the account-loss race). This
        /// mirrors <see cref="TeardownForSwitchAsync"/>: abort the socket FIRST, then Dispose, AWAITED to completion, so the
        /// session file is flushed + its handle RELEASED (and the server-side connection closed) BEFORE the caller reopens it.
        /// Bounded so a hung dispose can't stall the switch.</summary>
        public async System.Threading.Tasks.Task DisposeWarmServiceAsync(int timeoutMs = 10000)
        {
            TearingDown = true;
            try { if (Updates != null) Updates.SaveState(UpdateStatePath); } catch { }
            try { if (Avatars != null) Avatars.Dispose(); } catch { }
            try { StopConnectionWatchdog(); } catch { }
            try { CancelAllDownloads("warm-drop"); } catch { }
            var client = Client;
            Client = null; Updates = null; Me = null;
            if (client != null)
            {
                var teardown = System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await client.ResetAsync(false, false); } catch { }   // abort the socket FIRST → clean, non-hanging dispose
                    try { client.Dispose(); } catch { }                        // flush session + RELEASE the file handle
                });
                var done = await System.Threading.Tasks.Task.WhenAny(teardown, System.Threading.Tasks.Task.Delay(timeoutMs));
                if (done != teardown) SwallowTaskFault(teardown);
            }
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

        private int _savingState;   // 0/1 non-reentrant guard so overlapping saves never stack blocking get_State() waits

        /// <summary>Persists the manager's update state so a restart resumes (recovers) from where it left off.
        /// DEADLOCK CONTRACT (SAVESTATE-DEADLOCK): SaveState reads UpdateManager.get_State(), which acquires WTC's
        /// update-state SemaphoreSlim — the SAME lock GetDifference holds during the post-connect seed. Called
        /// SYNCHRONOUSLY on the UI thread inside that seed (as the inlined GetDifference→LoadDialogs continuation), it
        /// self-deadlocks (non-reentrant semaphore). So callers MUST run it via Task.Run — OFF the UI thread, where the
        /// pool thread simply waits for the lock to free once the sync finishes. Idempotent: skips if a save is running.</summary>
        public void SaveUpdateState()
        {
            if (System.Threading.Interlocked.Exchange(ref _savingState, 1) == 1) return;   // a save is already in flight
            try { if (Updates != null) Updates.SaveState(UpdateStatePath); }
            catch { /* best-effort */ }
            finally { System.Threading.Interlocked.Exchange(ref _savingState, 0); }
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

        /// <summary>BATCH-TA-14: dialogs for a SPECIFIC set of peers (messages.getPeerDialogs) — the targeted
        /// fetch that lets a custom folder show members the paged main list never reached.
        /// The caller already holds real <see cref="InputPeer"/>s (a filter's pinned_peers/include_peers carry
        /// access_hash), so wrapping them is the entire conversion — no username lookup, no getFullChat.
        /// ⚠ ADAPTER, AND IT IS LOAD-BEARING: Messages_PeerDialogs is NOT a Messages_DialogsBase — both derive
        /// from Object, verified by reflection — so the response cannot be handed to BuildDialogEntries
        /// directly. TL.Messages_Dialogs carries exactly the same four fields, so copying them across lets the
        /// result flow through the EXISTING, already-correct entry builder. That matters: BuildDialogEntries
        /// seeds 22 fields, while the one hand-rolled alternative in the tree (EntryFromPeerInfo) seeds 5 and
        /// leaves Date=default, which sinks a row to the bottom of the list with no unread badge.
        /// Returns null for an empty/failed request so the caller can no-op rather than merge nothing.</summary>
        public async Task<Messages_DialogsBase> GetPeerDialogsAsync(InputPeer[] peers)
        {
            if (peers == null || peers.Length == 0) return null;
            var wrapped = new InputDialogPeerBase[peers.Length];
            for (int i = 0; i < peers.Length; i++) wrapped[i] = new InputDialogPeer { peer = peers[i] };

            var res = await Client.Messages_GetPeerDialogs(wrapped).ConfigureAwait(false);
            if (res == null) return null;
            return new Messages_Dialogs
            {
                dialogs = res.dialogs,
                messages = res.messages,
                chats = res.chats,
                users = res.users
            };
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

        // ── NOTIFY-BACKGROUND (STEP 2): per-account effective-mute, so a WARM/background account decides its OWN
        // notifications (its per-dialog notify_settings from the warm dialog snapshot, else its category default) —
        // identical rules to the active account, resolved against THIS account's own state. ──
        private bool _notifyDefaultsLoaded;
        public DateTime MuteDefUsers = DateTime.MinValue, MuteDefChats = DateTime.MinValue, MuteDefBroadcasts = DateTime.MinValue;

        // NOTIFY-BG-MUTE-FIX: per-peer notify_settings captured from LIVE UpdateNotifySettings (any time after warm-up).
        // The CachedDialogs snapshot is frozen at seed → a mute changed later isn't in it; this map is checked FIRST so
        // the background mute-gate honors current mutes without a re-warm. Written from the UM thread + UI thread → locked.
        private readonly Dictionary<long, PeerNotifySettings> _liveNotify = new Dictionary<long, PeerNotifySettings>();

        /// <summary>Fetch this account's category mute defaults (users/chats/broadcasts) ONCE — a warm/background account
        /// needs them to compute its own effective-mute (the active account fetches its own separately). Best-effort.</summary>
        public async Task LoadNotifyDefaultsAsync()
        {
            if (_notifyDefaultsLoaded || Client == null) return;
            try
            {
                MuteDefUsers = MuteUntilOf(await GetNotifyDefaultsAsync(new InputNotifyUsers()).ConfigureAwait(false));
                MuteDefChats = MuteUntilOf(await GetNotifyDefaultsAsync(new InputNotifyChats()).ConfigureAwait(false));
                MuteDefBroadcasts = MuteUntilOf(await GetNotifyDefaultsAsync(new InputNotifyBroadcasts()).ConfigureAwait(false));
                _notifyDefaultsLoaded = true;
                System.Diagnostics.Debug.WriteLine("[NOTIFY-BG] defaults loaded id=" + AccountId + " muted(u/c/b)="
                    + (MuteDefUsers > DateTime.UtcNow) + "/" + (MuteDefChats > DateTime.UtcNow) + "/" + (MuteDefBroadcasts > DateTime.UtcNow));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[NOTIFY-BG] defaults id=" + AccountId + " failed: " + ex.Message); }
        }

        private static DateTime MuteUntilOf(PeerNotifySettings ns)
            => ns != null && (ns.flags & PeerNotifySettings.Flags.has_mute_until) != 0 ? ns.mute_until : DateTime.MinValue;

        /// <summary>NOTIFY-BG-MUTE-FIX: apply a LIVE <c>UpdateNotifySettings</c> to THIS account's own mute state so its
        /// background mute-gate stays current after warm-up (the CachedDialogs snapshot is frozen at seed; the category
        /// defaults load once). Per-peer → the live override map (IsPeerEffectivelyMuted reads it BEFORE the snapshot);
        /// category → the MuteDef* defaults. Routed for the ACTIVE account too (so its map is fresh when it later
        /// backgrounds) and for background accounts (a mute changed while backgrounded applies without a re-warm).</summary>
        public void ApplyNotifyUpdate(NotifyPeerBase peer, PeerNotifySettings ns)
        {
            if (peer is NotifyPeer np && np.peer != null) { lock (_liveNotify) _liveNotify[np.peer.ID] = ns; }
            else if (peer is NotifyUsers) MuteDefUsers = MuteUntilOf(ns);
            else if (peer is NotifyChats) MuteDefChats = MuteUntilOf(ns);
            else if (peer is NotifyBroadcasts) MuteDefBroadcasts = MuteUntilOf(ns);
        }

        /// <summary>Is <paramref name="peer"/> effectively muted for THIS account? Live per-peer override →
        /// warm dialog snapshot → this account's category default by peer-kind. Never throws.
        ///
        /// ⚠ BATCH-TA-26/B2 — IT NOW FAILS **CLOSED**: a peer we cannot answer for is reported MUTED, so the
        /// caller stays SILENT. It used to fail open (return false = not muted), and the same "if we don't
        /// know, notify anyway" assumption is exactly what let a muted chat notify from the active path for
        /// so long. For a notification gate the asymmetry is not close: a missed notification is an
        /// inconvenience the unread badge still records, while notifying a chat the user explicitly silenced
        /// is the app ignoring an instruction. Unknown means SILENT.
        /// The only inputs that can be missing are a null peer or a genuine exception; the category default
        /// is always available once LoadNotifyDefaultsAsync has run, and TA-26/B3 seeds the per-peer map
        /// from account.getNotifyExceptions so "not in our dialog snapshot" is no longer an unknown.</summary>
        public bool IsPeerEffectivelyMuted(Peer peer)
        {
            if (peer == null) return true;   // cannot answer → SILENT (see remarks)
            try
            {
                // NOTIFY-BG-MUTE-FIX: a LIVE per-peer override (from UpdateNotifySettings after warm-up) beats the frozen
                // CachedDialogs snapshot — so a mute changed while this account was active OR backgrounded is honored.
                PeerNotifySettings ns; bool live;
                lock (_liveNotify) live = _liveNotify.TryGetValue(peer.ID, out ns);
                if (!live) ns = DialogNotifySettings(peer);
                bool explicitPerDialog = ns != null && (ns.flags & PeerNotifySettings.Flags.has_mute_until) != 0;
                DateTime cat = CategoryDefaultFor(peer);
                bool muted = explicitPerDialog ? ns.mute_until > DateTime.UtcNow   // explicit per-dialog decides (overrides category)
                                               : cat > DateTime.UtcNow;            // else the peer-kind category default
                if (TelegArm.Helpers.Logger.Enabled)
                    System.Diagnostics.Debug.WriteLine("[BGMUTE] acct=" + AccountId + " peer=" + peer.ID
                        + " src=" + (live ? "live" : ns != null ? "snapshot" : "category")
                        + " perDialogMute=" + (explicitPerDialog ? (ns.mute_until > DateTime.UtcNow).ToString() : "-")
                        + " categoryDefault=" + (cat > DateTime.UtcNow) + " effectiveMuted=" + muted);
                return muted;
            }
            catch { return true; }   // ⚠ fail CLOSED — see remarks
        }

        /// <summary>BATCH-TA-26/B3 — seed the per-peer notify map from <c>account.getNotifyExceptions</c>.
        ///
        /// WHY THIS EXISTS. The mute gate previously resolved a chat from the UI's `_allChats`, which holds
        /// ONE page of ~100 dialogs until the list is scrolled — so a muted chat further down simply wasn't
        /// found and the gate failed open. Even after routing the gate here, the two remaining sources are
        /// the CachedDialogs SNAPSHOT (also partial, and frozen at seed) and the category default. This is
        /// the missing input: ONE round-trip that returns every peer whose notify settings DIFFER from the
        /// category default — i.e. the authoritative exception list, with no dependence on how far any list
        /// has paged. Passing a null peer asks for all of them.
        /// Best-effort by design: a failure leaves the map as it was, and the gate still has the snapshot
        /// and the category defaults to work from.</summary>
        public async Task<int> SeedNotifyExceptionsAsync()
        {
            if (Client == null) return 0;
            try
            {
                var res = await Client.Account_GetNotifyExceptions(null, false, false).ConfigureAwait(false);
                int n = 0;
                var list = res is UpdatesCombined uc ? uc.updates : (res as Updates)?.updates;
                if (list != null)
                    foreach (var u in list)
                        if (u is UpdateNotifySettings uns) { ApplyNotifyUpdate(uns.peer, uns.notify_settings); n++; }
                TelegArm.Helpers.Logger.Diag("[NOTIFY] acct=" + AccountId + " notify exceptions seeded: " + n);
                return n;
            }
            catch (Exception ex)
            {
                TelegArm.Helpers.Logger.Diag("[NOTIFY] acct=" + AccountId + " notify exceptions FAILED: " + ex.Message);
                return 0;
            }
        }

        private PeerNotifySettings DialogNotifySettings(Peer peer)
        {
            var dlgs = CachedDialogs;
            if (dlgs == null) return null;
            long id = peer.ID;
            foreach (var d in dlgs.Dialogs)
                if (d != null && d.Peer != null && d.Peer.ID == id) return (d as Dialog)?.notify_settings;
            return null;
        }

        private DateTime CategoryDefaultFor(Peer peer)
        {
            if (peer is PeerUser) return MuteDefUsers;
            if (peer is PeerChat) return MuteDefChats;                // basic group
            if (peer is PeerChannel)
            {
                try { var info = Updates != null ? Updates.UserOrChat(peer) : null; if (info is Channel ch && (ch.flags & Channel.Flags.broadcast) != 0) return MuteDefBroadcasts; } catch { }
                return MuteDefChats;                                  // megagroup (or unresolved) → chats
            }
            return MuteDefChats;
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

        /// <summary>COMMENTS: the discussion (comments) thread for a channel post — carries the linked group + thread
        /// top (in `messages`) + read watermarks + the chats/users dicts for resolving reply authors.</summary>
        public Task<Messages_DiscussionMessage> GetDiscussionMessageAsync(InputPeer channel, int msgId)
            => Client.Messages_GetDiscussionMessage(channel, msgId);

        /// <summary>COMMENTS: page a channel post's comment thread. peer = the DISCUSSION GROUP, msg_id = the thread
        /// root's id IN THAT GROUP (the auto-forwarded post, from GetDiscussionMessage.messages) — this scopes to the
        /// one post's comments. Same offset-id paging as GetHistory, so the island pager translates 1:1.</summary>
        public Task<Messages_MessagesBase> GetRepliesAsync(InputPeer peer, int msgId, int limit = 50, int offsetId = 0, int addOffset = 0)
            => Client.Messages_GetReplies(peer, msgId, offset_id: offsetId, offset_date: default(DateTime),
                                          add_offset: addOffset, limit: limit, max_id: 0, min_id: 0, hash: 0);

        /// <summary>COMMENTS: mark a post's comment thread read up to read_max_id (best-effort; never surfaces).</summary>
        public async Task ReadDiscussionAsync(InputPeer channel, int msgId, int readMaxId)
        {
            try { await Client.Messages_ReadDiscussion(channel, msgId, readMaxId); } catch { }
        }

        /// <summary>FORUM-TOPICS: all NON-deleted topics of a forum group, for the topic bar. Uses the WTC paging helper.
        /// Empty list on failure. Reuse map — a topic is a thread keyed by <c>ForumTopic.id</c> (= its root message id):
        /// load it via <see cref="GetRepliesAsync"/>(forumPeer, topic.id), post via <see cref="SendThreadCommentAsync"/>
        /// (forumPeer, topic.id, text), mark read via <see cref="ReadDiscussionAsync"/>(forumPeer, topic.id, readMax).</summary>
        public async Task<System.Collections.Generic.List<ForumTopic>> GetForumTopicsAsync(InputPeer forumPeer)
        {
            var list = new System.Collections.Generic.List<ForumTopic>();
            var client = Client;
            if (client == null || forumPeer == null) return list;
            try
            {
                var res = await client.Channels_GetAllForumTopics(forumPeer, null).ConfigureAwait(false);
                if (res != null && res.topics != null)
                    foreach (var t in res.topics)
                        if (t is ForumTopic ft) list.Add(ft);   // skip ForumTopicDeleted
            }
            catch { }
            return list;
        }

        // ── Stories (STORIES-BUILD-1) ────────────────────────────────────────────────────────────────────
        // WTC 4.4.6 has the full Stories schema (layer 225) but NO Client.Stories_* convenience wrappers — the
        // 33 functions live in TL.Methods and go through the public Client.Invoke(IMethod<T>). Thin wrappers,
        // same shape as GetForumTopicsAsync above.

        /// <summary>The story TRAY feed: peers you follow with active stories (+ their max_read_id → unseen ring).
        /// Pass the cached <paramref name="state"/> on refresh. A null return means "not modified" (server said
        /// nothing changed — keep the current tray) OR no client / error — the caller keeps its existing tray.</summary>
        public async Task<Stories_AllStories> GetAllStoriesAsync(string state = null)
        {
            var client = Client;
            if (client == null) return null;
            try
            {
                var fn = new TL.Methods.Stories_GetAllStories();
                if (!string.IsNullOrEmpty(state)) { fn.flags = TL.Methods.Stories_GetAllStories.Flags.has_state; fn.state = state; }
                var res = await client.Invoke(fn).ConfigureAwait(false);
                return res as Stories_AllStories;   // null when Stories_AllStoriesNotModified
            }
            catch { return null; }
        }

        /// <summary>One peer's current stories (full StoryItems with media) — for the viewer (next batch).</summary>
        public async Task<Stories_PeerStories> GetPeerStoriesAsync(InputPeer peer)
        {
            var client = Client;
            if (client == null || peer == null) return null;
            try { return await client.Invoke(new TL.Methods.Stories_GetPeerStories { peer = peer }).ConfigureAwait(false); }
            catch { return null; }
        }

        /// <summary>A peer's PINNED / profile "Posted Stories" — the ones kept on the profile page, which PERSIST past
        /// the 24h active window (unlike GetPeerStories = the transient story ring). stories.getPinnedStories →
        /// Stories_Stories.stories (full StoryItems). This is the source for the profile's "POSTED STORIES" grid. Null on failure.</summary>
        public async Task<Stories_Stories> GetPinnedStoriesAsync(InputPeer peer, int offsetId = 0, int limit = 100)
        {
            var client = Client;
            if (client == null || peer == null) return null;
            try { return await client.Invoke(new TL.Methods.Stories_GetPinnedStories { peer = peer, offset_id = offsetId, limit = limit }).ConfigureAwait(false); }
            catch { return null; }
        }

        /// <summary>Marks a peer's stories seen up to <paramref name="maxId"/> (dims the ring); returns read ids.</summary>
        public async Task<int[]> ReadStoriesAsync(InputPeer peer, int maxId)
        {
            var client = Client;
            if (client == null || peer == null) return null;
            try { return await client.Invoke(new TL.Methods.Stories_ReadStories { peer = peer, max_id = maxId }).ConfigureAwait(false); }
            catch { return null; }
        }

        /// <summary>STORIES-VIEW-REGISTER: registers YOUR view of specific stories — tells the POSTER you viewed
        /// (their viewer list + view count). Distinct from ReadStories (that's your own seen-state / ring-dim).
        /// Best-effort; fire-and-forget at the call site.</summary>
        public async Task<bool> IncrementStoryViewsAsync(InputPeer peer, int[] ids)
        {
            var client = Client;
            if (client == null || peer == null || ids == null || ids.Length == 0) return false;
            try { return await client.Invoke(new TL.Methods.Stories_IncrementStoryViews { peer = peer, id = ids }).ConfigureAwait(false); }
            catch { return false; }
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

        /// <summary>INCHAT-SEARCH: full-text search scoped to ONE peer (messages.search) — the matching messages in
        /// THAT conversation, newest-first. Distinct from SearchMessagesAsync (global SearchGlobal). offset_id=0 for
        /// the newest page; pass the oldest result id already shown to page OLDER matches. The result's .count is the
        /// total match count ("N messages found").</summary>
        public Task<Messages_MessagesBase> SearchInChatAsync(InputPeer peer, string query, int offsetId = 0, int limit = 40)
        {
            // The generic Client.Messages_Search<TFilter> needs a CONCRETE filter (there is no "empty" filter type),
            // so call the raw function via Invoke with filter left null — WTC serializes a null MessagesFilter as
            // inputMessagesFilterEmpty (all message kinds), the same convenience SearchGlobal relies on.
            // (top_msg_id is available here for a future forum-TOPIC-scoped search.)
            return Client.Invoke(new TL.Methods.Messages_Search { peer = peer, q = query, offset_id = offsetId, limit = limit });
        }

        /// <summary>SEARCH: public discovery. Contacts_Search finds PUBLIC channels/groups/users by name/username —
        /// my_results = your matching chats/contacts, results = global public matches (incl. entities you're NOT in).
        /// No offset in the API → "show more" re-queries with a larger limit.</summary>
        public Task<Contacts_Found> SearchContactsAsync(string query, int limit)
        {
            return Client.Contacts_Search(query, limit);
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

        /// <summary>MENTION-REACTION: marks unread @mentions/replies in a peer as read (clears unread_mentions_count).</summary>
        public Task ReadMentionsAsync(InputPeer peer) => Client.Messages_ReadMentions(peer);

        /// <summary>MENTION-REACTION: marks unread reactions-to-you in a peer as read (clears unread_reactions_count).</summary>
        public Task ReadReactionsAsync(InputPeer peer) => Client.Messages_ReadReactions(peer);

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

        /// <summary>Archived dialogs (folder_id = 1) — ALL of them.
        /// BATCH-TA-13/R1. This used to be `Client.Messages_GetDialogs(folder_id: 1)`: no offsets, no limit,
        /// i.e. ONE server-default page of ~100 — exactly what the note on <see cref="GetDialogsAsync"/> warns
        /// about. But unlike the main list, the archive has NO pager anywhere (LoadArchivedAsync is its only
        /// caller and it merges a single response), so an archive of more than ~100 dialogs silently lost the
        /// remainder, with nothing in the log to show it.
        /// WHY the WTC helper is acceptable HERE and not for the main list: the helper pages internally to
        /// completion and offers NO cancellation and NO progress callback (TA-12/B1), so it is only safe where
        /// the result set is small and bounded by nature. The archive is; the 627-dialog main list is not —
        /// there the scroll-driven pager stays, because one blocking sweep would trade a responsive list for a
        /// multi-second stall on ARM32.
        /// The count is logged so the truncation this fixes would have been PROVABLE before, and the new
        /// behaviour is provable now.</summary>
        public async Task<Messages_DialogsBase> GetArchivedDialogsAsync()
        {
            var res = await Client.Messages_GetAllDialogs(1).ConfigureAwait(false);
            if (TelegArm.Helpers.Logger.Enabled)
                TelegArm.Helpers.Logger.Diag("[ARCHIVE] getAllDialogs(folder 1) → " + (res?.Dialogs?.Length ?? 0)
                    + " dialog(s)" + (res?.Dialogs != null && res.Dialogs.Length > 100 ? "  ⚠ >100: the OLD single-page call would have TRUNCATED here" : ""));
            return res;
        }

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
        /// <summary>Blocks or unblocks a peer, and RETURNS WHETHER TELEGRAM ACTUALLY DID IT.
        ///
        /// ⚠ BATCH-TA-39 — THIS USED TO RETURN `Task` AND THROW THE ANSWER AWAY:
        ///     => blocked ? (Task)Client.Contacts_Block(peer) : Client.Contacts_Unblock(peer);
        ///   `contacts.block` and `contacts.unblock` return **Bool**, not void (checked against the shipped
        ///   WTelegramClient.dll, rail R8: `Contacts_Block(Client, InputPeer id, bool my_stories_from = opt)`
        ///   → `Task&lt;bool&gt;`). Casting to the non-generic Task awaited completion and DISCARDED the
        ///   result, so a request Telegram declined looked identical to one it honoured, and the caller
        ///   cheerfully reported "User blocked." That is half of why blocking appeared not to work.</summary>
        public Task<bool> SetBlockedAsync(InputPeer peer, bool blocked)
            => blocked ? Client.Contacts_Block(peer) : Client.Contacts_Unblock(peer);

        /// <summary>TA-39 — the AUTHORITATIVE blocked state for a user, from `UserFull.flags.blocked`.
        /// ProfileForm previously kept a plain bool that started false and was only ever written by its own
        /// toggle, so an already-blocked user showed "Block user" and clicking it was a no-op the UI still
        /// reported as success. This is the same field ComposerState.Resolve already trusts, so the profile
        /// and the composer can no longer disagree about whether someone is blocked.</summary>
        public async Task<bool> IsBlockedAsync(User u)
        {
            if (u == null) return false;
            try
            {
                var full = await GetUserFullAsync(u).ConfigureAwait(false);
                return full != null && (full.flags & UserFull.Flags.blocked) != 0;
            }
            catch { return false; }
        }

        /// <summary>Renames a saved contact. contacts.addContact re-adds the EXISTING user (matched by id) with new
        /// names → Telegram treats it as an edit. Reuses the contact's known phone; the optional note /
        /// phone-privacy params are left at their defaults. Bounded + off-thread; false on timeout, throws on error.</summary>
        public Task<bool> EditContactAsync(User user, string firstName, string lastName, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () =>
            {
                await Client.Contacts_AddContact(new InputUser(user.id, user.access_hash),
                    firstName ?? "", lastName ?? "", user.phone ?? "");
                return true;
            }, timeoutMs, "EditContact");
        }

        /// <summary>Removes a saved contact (contacts.deleteContacts) — the user + chat history stay; they're just no
        /// longer in your contacts. Bounded + off-thread; false on timeout, throws on error (the caller reports it).</summary>
        public Task<bool> DeleteContactAsync(User user, int timeoutMs = 20000)
        {
            return AdminBoundedAsync(async () =>
            {
                await Client.Contacts_DeleteContacts(new InputUserBase[] { new InputUser(user.id, user.access_hash) });
                return true;
            }, timeoutMs, "DeleteContact");
        }

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
        // (Confirmed against WTC's open-source WTelegram.Encryption.Check2FA — github.com/wiz0u/WTelegramClient.) NEVER hand-roll SRP.

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

        /// <summary>Leaves a channel/supergroup (`channels.leaveChannel`) or a basic group (`messages.deleteChatUser` of self).
        ///
        /// ⚠ BATCH-TA-20/S0b — THE ELSE BRANCH USED TO BE `return Task.CompletedTask`, A SILENT NO-OP.
        /// Nothing was sent to the server, the call reported success, and both callers then ran their
        /// success path: MainForm.DeleteOrLeaveChat REMOVED THE ROW from the chat list and ProfileForm
        /// closed itself with LeftChat = true. So an unsupported peer type looked exactly like a completed
        /// leave, and the chat silently reappeared on the next sync with no error anywhere. It now LOGS and
        /// THROWS, which both call sites already handle — each catches, shows "Couldn't leave: …", and
        /// returns BEFORE its success side effects, so the row survives and the user is told.
        ///
        /// R8 NOTE, verified against the shipped DLL (IL 292296): WTelegramClient ships
        /// <c>Client.LeaveChat(InputPeer)</c> and its body is this dispatch line for line — same isinst
        /// order, same two calls, throwing ArgumentException on anything else. We keep our own copy for ONE
        /// reason: control of that last branch. WTC's exception text is surfaced straight to the user by
        /// the callers above, and "Invalid peer" is not something a person can act on. If that ever stops
        /// being true, switch to the helper.</summary>
        public Task LeaveChatAsync(InputPeer peer)
        {
            if (peer is InputPeerChannel ch)
                return Client.Channels_LeaveChannel(new InputChannel(ch.channel_id, ch.access_hash));
            if (peer is InputPeerChat pc)
                return Client.Messages_DeleteChatUser(pc.chat_id, InputUser.Self);

            TelegArm.Helpers.Logger.Diag("[LEAVE] REFUSED — peer type " + (peer == null ? "null" : peer.GetType().Name)
                                         + " cannot be left; nothing was sent to the server");
            throw new NotSupportedException("This kind of chat can't be left.");
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

        /// <summary>BATCH-TA-10/R2b: a FRESH read of the dialog filters, for the read-modify-write in
        /// MainForm.TogglePinInFolderAsync.
        /// ⚠ Unlike <see cref="GetDialogFiltersAsync"/> this does NOT swallow. There, an empty array is a
        /// benign "no folders" fallback; here it would be indistinguishable from "the folder was deleted
        /// on another device", so a network blip would report the wrong thing to the user. The caller needs
        /// to tell those two apart, so the exception has to reach it.</summary>
        public async Task<DialogFilterBase[]> GetDialogFiltersFreshAsync()
        {
            var r = await Client.Messages_GetDialogFilters();
            return (r as Messages_DialogFilters)?.filters ?? new DialogFilterBase[0];
        }

        /// <summary>BATCH-TA-10: writes one dialog filter back (messages.updateDialogFilter). Used only to
        /// change a folder's pinned_peers — see MainForm.TogglePin.
        /// ⚠ DELIBERATELY NOT try/catch'd, unlike <see cref="GetDialogFiltersAsync"/> above. This is a WRITE:
        /// swallowing the error would leave the caller believing the server accepted a folder edit it
        /// rejected, and the local folder object would then diverge from the server's. The caller must see
        /// the RpcException so it can surface the server's own message and roll back.
        /// Documented 400s include FILTER_ID_INVALID, FILTER_INCLUDE_EMPTY, FILTER_TITLE_EMPTY,
        /// PEER_ID_INVALID and CHATLIST_EXCLUDE_INVALID — the last three of which fire when the filter is
        /// reconstructed badly, which is why the caller copies every field verbatim.</summary>
        public Task<bool> UpdateDialogFilterAsync(int id, DialogFilterBase filter)
        {
            return Client.Messages_UpdateDialogFilter(id, filter);
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

        /// <summary>QUICKWINS-1: send our own typing / cancel action for a chat so contacts see us type. Fire-and-forget
        /// and error-swallowing — FLOOD_WAIT, or a peer that won't accept typing, must NEVER surface or block the UI.</summary>
        public async Task SendTypingAsync(InputPeer peer, bool typing)
        {
            if (peer == null || Client == null) return;
            SendMessageAction action = typing ? (SendMessageAction)new SendMessageTypingAction() : new SendMessageCancelAction();
            try { await Client.Messages_SetTyping(peer, action); }
            catch { /* typing is best-effort — never surface */ }
        }

        /// <summary>DRAFTS: save (or, with an EMPTY message, CLEAR) the unsent draft for a peer — messages.saveDraft.
        /// We store the RAW composer text (markdown re-parses on send), no entities/reply. Best-effort; false on any
        /// failure so fire-and-forget callers can ignore it. The server echoes UpdateDraftMessage (cross-device sync).</summary>
        public async Task<bool> SaveDraftAsync(InputPeer peer, string message)
        {
            var client = Client;
            if (client == null || peer == null) return false;
            try { return await client.Messages_SaveDraft(peer, message ?? "").ConfigureAwait(false); }
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
        public Task<Message> SendTextAsync(InputPeer peer, string text, int replyToMsgId = 0, MessageEntity[] entities = null)
        {
            return replyToMsgId > 0
                ? Client.SendMessageAsync(peer, text, reply_to_msg_id: replyToMsgId, entities: entities)
                : Client.SendMessageAsync(peer, text, entities: entities);
        }

        /// <summary>COMMENTS-POST: post a comment into a channel post's discussion thread. The SendMessageAsync helper
        /// exposes only reply_to_msg_id (no top_msg_id), so this uses the RAW Messages_SendMessage with
        /// InputReplyToMessage{top_msg_id}. peer = the DISCUSSION GROUP, rootMsgId = the thread root's GROUP-side id
        /// (the auto-forwarded post = GetDiscussionMessage.messages[last].ID) — the SAME addressing reads use, and the
        /// only form a non-admin can post through (sending to the broadcast channel is admin-only → CHAT_ADMIN_REQUIRED).
        /// Returns the sent Message parsed from the Updates (null if none), or throws (caller handles
        /// CHAT_WRITE_FORBIDDEN → join, FLOOD_WAIT, etc.).</summary>
        public async Task<Message> SendThreadCommentAsync(InputPeer groupPeer, int rootMsgId, string text, MessageEntity[] entities = null)
        {
            var reply = new InputReplyToMessage
            {
                flags = InputReplyToMessage.Flags.has_top_msg_id,
                top_msg_id = rootMsgId,
                reply_to_msg_id = rootMsgId   // a top-level comment replies to the thread root (the group-side forwarded post)
            };
            var updates = await Client.Messages_SendMessage(groupPeer, text, WTelegram.Helpers.RandomLong(), reply_to: reply, entities: entities);
            return SentMessageFrom(updates);
        }

        /// <summary>Extracts the just-sent Message from a Messages_SendMessage Updates result (channel/group → the
        /// UpdateNewChannelMessage; falls back to UpdateNewMessage). Null when the result carries no full message.</summary>
        private static Message SentMessageFrom(UpdatesBase updates)
        {
            if (updates is Updates u && u.updates != null)
                foreach (var upd in u.updates)
                {
                    if (upd is UpdateNewChannelMessage ncm && ncm.message is Message m1) return m1;
                    if (upd is UpdateNewMessage nm && nm.message is Message m2) return m2;
                }
            return null;
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

        /// <summary>SEARCH-sponsored: promoted channels/bots for a search query (distinct from in-channel sponsored
        /// messages). Null/empty when there's no sponsored inventory. Each SponsoredPeer carries a random_id for the
        /// SAME view/click reporting (ViewSponsoredAsync/ClickSponsoredAsync).</summary>
        public Task<Contacts_SponsoredPeers> GetSponsoredPeersAsync(string query)
        {
            return Client.Contacts_GetSponsoredPeers(query);
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
        public Task<UpdatesBase> EditMessageAsync(InputPeer peer, int id, string text, MessageEntity[] entities = null)
        {
            return Client.Messages_EditMessage(peer, id, message: text, entities: entities);
        }

        /// <summary>Updates my own profile (first/last name + bio).</summary>
        /// <summary>PROFILE-EDIT-SELF: save first/last name + bio (Account_UpdateProfile) and refresh <see cref="Me"/>
        /// from the returned self User so the app's displayed name updates. Empty args leave that field unchanged.</summary>
        public async Task<bool> UpdateProfileAsync(string firstName, string lastName, string about)
        {
            var res = await Client.Account_UpdateProfile(firstName, lastName, about).ConfigureAwait(false);
            if (res is User u) Me = u;
            return true;
        }

        /// <summary>My own bio/about text (empty on failure).</summary>
        public async Task<string> GetSelfAboutAsync()
        {
            try
            {
                var full = await Client.Users_GetFullUser(InputUser.Self);
                return full?.full_user?.about ?? "";
            }
            catch { return ""; }
        }

        // ── PROFILE-EDIT-SELF: username (check-then-set) + profile photo (add / remove) + self refresh ──

        /// <summary>Availability check for MY username (account.checkUsername) — true = free. Check BEFORE saving.</summary>
        public async Task<bool> CheckSelfUsernameAsync(string username)
        {
            var client = Client;
            if (client == null) return false;
            return await client.Account_CheckUsername(username ?? "").ConfigureAwait(false);
        }

        /// <summary>Sets MY @username (account.updateUsername) — call ONLY after CheckSelfUsername said available.
        /// THROWS on USERNAME_OCCUPIED (a save-time race) so the caller can report it; refreshes <see cref="Me"/> on success.</summary>
        public async Task<bool> UpdateSelfUsernameAsync(string username)
        {
            var res = await Client.Account_UpdateUsername(username ?? "").ConfigureAwait(false);
            if (res is User u) Me = u;
            return true;
        }

        /// <summary>Uploads a local image and sets it as MY profile photo (photos.uploadProfilePhoto), then refreshes
        /// <see cref="Me"/> so the new photo propagates. Streams from disk (ARM32-safe). False on no client/path.</summary>
        public async Task<bool> SetProfilePhotoAsync(string filePath)
        {
            var client = Client;
            if (client == null || string.IsNullOrEmpty(filePath)) return false;
            var uploaded = await client.UploadFileAsync(filePath, null).ConfigureAwait(false);
            await client.Photos_UploadProfilePhoto(file: uploaded).ConfigureAwait(false);
            await RefreshMeAsync().ConfigureAwait(false);
            return true;
        }

        /// <summary>My profile photos (photos.getUserPhotos on self) — the Photo objects, so a caller can build the
        /// InputPhoto to delete. Null on failure.</summary>
        public async Task<Photos_Photos> GetSelfPhotosAsync(int limit = 12)
        {
            var client = Client;
            if (client == null || Me == null) return null;
            try { return await client.Photos_GetUserPhotos(new InputUser(Me.id, Me.access_hash), 0, 0, limit).ConfigureAwait(false); }
            catch { return null; }
        }

        /// <summary>Removes ONE profile photo (photos.deletePhotos with its InputPhoto) then refreshes <see cref="Me"/>
        /// so the now-current photo (or none → letter avatar) propagates. False on no client/photo.</summary>
        public async Task<bool> DeleteProfilePhotoAsync(Photo p)
        {
            var client = Client;
            if (client == null || p == null) return false;
            await client.Photos_DeletePhotos(new InputPhoto[] { new InputPhoto { id = p.id, access_hash = p.access_hash, file_reference = p.file_reference } }).ConfigureAwait(false);
            await RefreshMeAsync().ConfigureAwait(false);
            return true;
        }

        /// <summary>Re-fetches the self User (users.getFullUser on self) and updates <see cref="Me"/> — so name/photo
        /// edits reflect in Me for the app's avatar/name reads. Best-effort.</summary>
        public async Task RefreshMeAsync()
        {
            try
            {
                var full = await Client.Users_GetFullUser(InputUser.Self).ConfigureAwait(false);
                if (full?.users != null && Me != null && full.users.TryGetValue(Me.id, out var ub) && ub is User u) Me = u;
            }
            catch { }
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

        /// <summary>PROFILE-CHANNEL: a user's attached personal channel (UserFull.personal_channel_id) + its latest
        /// post (personal_channel_message). Returns null when the user has none. The channel rides the GetFullUser
        /// chats dict; the latest message needs a follow-up Channels_GetMessages (best-effort — null preview on fail).</summary>
        public async Task<(Channel channel, Message latest, int subs)?> GetPersonalChannelAsync(User u)
        {
            if (u == null || Client == null) return null;
            try
            {
                var full = await Client.Users_GetFullUser(new InputUser(u.id, u.access_hash));
                var fu = full?.full_user;
                if (fu == null || fu.personal_channel_id == 0) return null;   // id != 0 ⇒ the has_personal_channel_id flag was set
                if (full.chats == null || !full.chats.TryGetValue(fu.personal_channel_id, out var cb) || !(cb is Channel ch)) return null;
                Message latest = null;
                if (fu.personal_channel_message != 0)
                {
                    try
                    {
                        var res = await Client.Channels_GetMessages(new InputChannel(ch.id, ch.access_hash),
                            new InputMessageID { id = fu.personal_channel_message });
                        if (res?.Messages != null)
                            foreach (var mb in res.Messages) { if (mb is Message m) { latest = m; break; } }
                    }
                    catch { }
                }
                return (ch, latest, ch.participants_count);
            }
            catch { return null; }
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
            // WIZOU-REVIEW 1.4 (download cancellation): abort in-flight downloads on APP-EXIT so each aborts cleanly
            // (flag → OperationCanceledException, .part kept) BEFORE the client is disposed — avoiding the disposal
            // race + any hang/leak. WTC 4.4.6's DownloadFileAsync/Upload_GetFile take NO CancellationToken, so this
            // flag-based Cancel IS our equivalent (checked between chunks + in the progress callback). Account-switch
            // already calls CancelAllDownloads; this covers the app-close path.
            try { CancelAllDownloads("app-exit"); } catch { }
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
            // BATCH-TA-4 (A3): the teardown had NO trace at all, which is why the device run's two opens of one
            // session path 71 s apart could only be explained by INFERENCE ("the add-account flow must have torn
            // the first client down in between") rather than proven from the log. These two lines make the
            // release an observable event, so the [SESSPATH] same-path fingerprint becomes decidable: two opens
            // of one path with a teardown between them is correct; without one it is the two-client bug.
            TelegArm.Helpers.Logger.Diag("[ACCT] teardown ENTER acct=" + AccountId + " session=\"" + SessionPath + "\" (switch: keeps the session, releases the handle)");
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
                if (done == teardown) TelegArm.Helpers.Logger.Diag("[ACCT] teardown complete (session flushed, handle released)");
                else { TelegArm.Helpers.Logger.Diag("[ACCT] WARNING: teardown TIMED OUT (" + timeoutMs + "ms) — session handle may linger briefly (two-client risk on the next open)"); SwallowTaskFault(teardown); }
            }
            // Unconditional exit rung — covers the client==null path too, so every teardown is bracketed in the log.
            TelegArm.Helpers.Logger.Diag("[ACCT] teardown EXIT acct=" + AccountId + " hadClient=" + (client != null));
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
            // R7: logout is the ONLY legitimate account-deleting path in the app, so its trace must survive
            // Release — otherwise a device log cannot distinguish "the user logged out" from "something
            // deleted an account on its own", which is precisely the question rail R4 exists to answer.
            TelegArm.Helpers.Logger.Diag("[ACCT] logout START id=" + id + " (explicit user action; this path DOES delete)");
            TelegArm.Helpers.Logger.Diag("[LOGOUT-TRACE] BeginLogoutCleanup: before StopConnectionWatchdog");
            StopConnectionWatchdog();
            // Deliberately SKIP Updates.SaveState — the account (and its update-state file) is being DELETED, and
            // SaveState can BLOCK on an internal UpdateManager lock held by the update loop stuck on the dead VPN.
            TelegArm.Helpers.Logger.Diag("[LOGOUT-TRACE] BeginLogoutCleanup: after StopWatchdog (skipped SaveState); before null handles");
            var client = Client;
            Client = null; Updates = null; Me = null;
            _silentResume = false; NeedsInteractiveLogin = false;
            // Mark this account "deleting" NOW (synchronously) so a concurrent switch/corrupt-recovery skips it as
            // a candidate — it must never try to resume an account whose files the background cleanup is removing.
            AccountStore.MarkDeleting(id);
            TelegArm.Helpers.Logger.Diag("[LOGOUT-TRACE] BeginLogoutCleanup: after null handles; firing background cleanup");

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
                    TelegArm.Helpers.Logger.Diag("[ACCT] logout cleanup DONE id=" + id + " (accounts/" + id + " + Cache/" + id + " deleted by user logout)");
                }
                catch (Exception ex) { TelegArm.Helpers.Logger.Diag("[ACCT] logout cleanup ERROR id=" + id + ": " + ex.Message); }
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
