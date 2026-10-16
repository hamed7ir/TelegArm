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
        /// <summary>Removes the ACTIVE account (distinct from switching, which keeps it): auth.logOut on the
        /// server → delete accounts/{id}/ + Cache/{id}/ → switch to another account, or the LoginForm if none.</summary>
        private void LogOut()
        {
            long id = AccountContext.ActiveId;
            string name = _service.Me != null ? DisplayName(_service.Me) : "this account";
            int c = ThemedDialog.Show(this, "Log Out",
                "Log out of " + name + "?\n\nThis removes the account and deletes its session and cache on this device. (Switching accounts keeps it.)",
                "Log Out", "Cancel");
            try { ActiveControl = null; } catch { }
            if (c != 0) return;

            // INSTANT: detach the client + run ALL the slow work (server logout, dispose, delete folders) in the
            // BACKGROUND, then transition the UI right away — never wait on the network (the freeze fix).
            // [LOGOUT-TRACE] on every UI-thread step → the LAST line printed before a freeze IS the blocker.
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] A confirmed, id=" + id);
            var others = AccountStore.ListAccounts().FindAll(a => a.Id != id);
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] B listed accounts, remaining=" + others.Count);
            _service.BeginLogoutCleanup(id);   // detach + bg: best-effort Auth_LogOut + dispose + delete accounts/{id} & Cache/{id}
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] C after BeginLogoutCleanup (client detached, cleanup running in bg)");
            ResetPerAccountState();
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] D after ResetPerAccountState");
            AuthManager.Reset();
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] E after AuthManager.Reset");

            if (others.Count > 0)
            {
                AccountContext.ActiveId = 0;
                AccountStore.WriteActive(others[0].Id);
                System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] F before SwitchAccountAsync → " + others[0].Id);
                var ignore = SwitchAccountAsync(others[0].Id, others[0].Name);   // switch to a remaining account (resilient connect)
                System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] G SwitchAccountAsync returned (running async)");
            }
            else
            {
                AccountContext.ActiveId = 0;
                AccountStore.WriteActive(0);
                System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] F before ShowLoginForm (no remaining accounts)");
                ShowLoginForm();   // no accounts left → first-launch login (instant)
                System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] G after ShowLoginForm");
            }
        }

        private async void SwitchAccount(long id, string name)
        {
            try { await SwitchAccountAsync(id, name); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[ACCT] switch err: " + ex.Message); }
        }

        /// <summary>Adds another account: teardown the active client → login on accounts/_pending/ → on success
        /// relocate to accounts/{newId}/ and rebuild; an already-added id just switches; cancel restores the
        /// previous account.</summary>
        private async void AddAccount()
        {
            CloseDrawer();
            long prev = AccountContext.ActiveId;
            string prevName = _service.Me != null ? DisplayName(_service.Me) : null;

            // FULL isolation from the previous account so the add login is a GENUINE fresh login (not a resume
            // of a stale identity): drop the client + Me, clear AuthManager (phone/code/password), point the
            // session at an EMPTY accounts/_pending/, and robustly remove any leftover _pending session.
            await _service.TeardownForSwitchAsync();         // disposes the active client; nulls Me + _silentResume
            ResetPerAccountState();
            AuthManager.Reset();                  // no previous phone/identity leaks into the add login
            AccountContext.ActiveId = 0;          // → Config("session_pathname") = accounts/_pending/session
            _service.AccountId = 0;               // CRITICAL (ACCOUNT-SESSION-PATH-FIX): TelegramService.SessionPath checks
                                                  // this.AccountId FIRST — without this reset the reused _service still holds the
                                                  // PREVIOUS account's id, so the add-login opens accounts/{prevId}/session (that
                                                  // account's REAL file) and Telegram rebinds it → the previous account is LOGGED
                                                  // OUT. Pair with ActiveId=0 so the pending login resolves to _pending.
            AccountContext.LegacyMode = false;
            AccountStore.ClearPending();          // a leftover _pending session would be RESUMED → wrong identity
            if (AccountStore.PendingSessionExists()) System.Diagnostics.Debug.WriteLine("[ACCT] WARNING: stale _pending session still present before add");
            System.Diagnostics.Debug.WriteLine("[ACCT] add-account: Me nulled + AuthManager reset + session=" + AccountContext.SessionPath);

            bool added = false; long newId = 0; string newName = null;
            using (var login = new LoginForm(_service) { AddMode = true })
            {
                var result = login.ShowDialog(this);
                if (result == DialogResult.OK && _service.Me != null)
                {
                    added = true; newId = _service.Me.id; newName = DisplayName(_service.Me);
                    System.Diagnostics.Debug.WriteLine("[ACCT] add login completed, new Me.id=" + newId + " (" + newName + "); previous active=" + prev);
                }
            }

            if (!added)
            {
                // CANCEL / BACK-OUT / FAILURE → fully non-destructive: discard ONLY accounts/_pending/ (never a
                // real account or Cache), clear the stale add-attempt phone so the restore silently resumes,
                // and return to the previously-active account (or another existing one — never empty).
                AccountStore.ClearPending();
                AuthManager.Reset();
                long restore = prev;
                if (restore == 0) { var accs = AccountStore.ListAccounts(); if (accs.Count > 0) restore = accs[0].Id; }
                System.Diagnostics.Debug.WriteLine("[ACCT] add cancelled → deleted _pending only, restoring active=" + restore);
                // Pair AccountId with ActiveId=0 (the pending invariant) so nothing resolves a stale session between here
                // and the switch; SwitchAccountAsync then re-binds _service.AccountId to `restore`.
                if (restore != 0) { AccountContext.ActiveId = 0; _service.AccountId = 0; await SwitchAccountAsync(restore, prevName); }
                else ShowLoginForm();   // genuinely no accounts (e.g. add was the very first) → first-launch login (resets AccountId)
                return;
            }

            if (AccountStore.Exists(newId))
            {
                // Already added → discard the new (duplicate) session, switch to the existing one.
                System.Diagnostics.Debug.WriteLine("[ACCT] add-account: " + newId + " already present → switching");
                await _service.LogOutServerAsync();   // revoke the duplicate pending session
                await _service.TeardownForSwitchAsync();
                AccountStore.ClearPending();
                AccountContext.ActiveId = 0;          // ≠ newId → switch guard passes (even if re-adding the active account)
                _service.AccountId = 0;               // pair with ActiveId=0 (pending invariant); SwitchAccountAsync re-binds it to newId
                await SwitchAccountAsync(newId, newName);
                return;
            }

            // New account → relocate pending → accounts/{newId}/ and reconnect there.
            System.Diagnostics.Debug.WriteLine("[ACCT] add-account new session id=" + newId);
            await _service.TeardownForSwitchAsync();
            AccountStore.RelocatePendingToAccount(newId, newName);
            AccountContext.ActiveId = newId;
            AccountStore.WriteActive(newId);
            ShowConnecting("Loading " + newName + "…");
            bool connected = await ConnectResilientlyAsync("Loading " + newName + "…");
            if (!connected) { await OnConnectFailedAsync(); return; }
            HideConnecting();
            await AfterConnectAsync();
        }

        // ── Login + initial load ─────────────────────────────────────────────

        /// <summary>The LIVE rig only (ShotRig.RunLive): the demo window is shown with no session — no resume, so no
        /// fall-back to the login window. Never set anywhere else.</summary>
        internal static bool DemoNoSession;

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (DemoNoSession) return;
            PerfLog.Boot("MainForm.OnLoad → ResumeSessionAsync");
            await ResumeSessionAsync();

            // ⚠ TA-37 — DELIVER A CLICK THAT LAUNCHED US. When the app was closed, Windows started this
            //   process to serve the activation, so ToastActivator.Activate ran long before OnLoad and had
            //   no handler to call; it parked the result instead. Draining AFTER ResumeSessionAsync matters:
            //   OpenNotifiedChat needs the chat list and, for a background account, a switch — neither of
            //   which exists until the session is up. Without this the single most important case (click
            //   while closed) opens the app to no chat at all.
            try { ToastActivator.DrainPending(); } catch { }
        }

        /// <summary>
        /// Resumes the saved session. Stays on MainForm whenever the client ends up
        /// authorized; only drops to LoginForm for genuine auth failures.
        /// </summary>
        private async System.Threading.Tasks.Task ResumeSessionAsync()
        {
            // Connection at launch is reached ONLY via the VPN, which may not be up yet — so a failure is a
            // NORMAL, recoverable state. NEVER exit/give up: retry forever with a capped backoff, showing a
            // "Connecting… / waiting for network" overlay (+ a manual Retry), until the session resumes.
            // CRITICAL: WTelegram's connect BLOCKS (doesn't throw) when there's no network, so every attempt
            // is bounded by a timeout — a hung connect is torn down and treated as a failed attempt.
            bool connected = await ConnectResilientlyAsync("Connecting to Telegram…");
            if (!connected) { await OnConnectFailedAsync(); return; }

            // The session may be at a TEMP location: the legacy flat file (migration) or accounts/_pending/
            // (a first login that landed on a new MainForm). Now that Me.id is known, relocate it (+ cache)
            // under accounts/{id}/ and re-resume there. One-time per account.
            if (_service.Me != null && (AccountContext.LegacyMode || !AccountContext.HasActive))
                if (!await FinalizeNewAccountAsync(AccountContext.LegacyMode)) { await OnConnectFailedAsync(); return; }

            HideConnecting();
            Logger.Diag("[CONN] connected — entering app");
            SetupTray();              // only once we're authorized and staying on MainForm
            await AfterConnectAsync();
        }

        /// <summary>The resilient connect loop (CONN-fix): retries forever on a missing/black-holed VPN with a
        /// "Connecting…/waiting" overlay + bounded attempts. Returns true when authorized; false when an
        /// interactive login is required (caller drops to the LoginForm). Shared by resume AND account switch.</summary>
        private async System.Threading.Tasks.Task<bool> ConnectResilientlyAsync(string firstMsg)
        {
            _connectCorrupt = false;   // fresh per connect; set true only on an unreadable-session error
            int backoffMs = TelegramService.ConnectInitialBackoffMs;
            int hardFailures = 0;          // consecutive NON-network failures (locked/unusable session) → capped, never infinite
            const int maxHardFailures = 5;
            for (int attempt = 1; ; attempt++)
            {
                if (_abortConnect != null && _abortConnect.IsCancellationRequested)   // user tapped Cancel on a switch
                { Logger.Diag("[ACCT] connect loop aborted by user"); return false; }

                if (_retryNowCts != null) { try { _retryNowCts.Dispose(); } catch { } }
                _retryNowCts = new System.Threading.CancellationTokenSource();   // "Retry now" cancels this (attempt + backoff)
                var token = _retryNowCts.Token;

                ShowConnecting(attempt == 1 ? firstMsg : "Waiting for network — make sure your VPN is on.");
                Logger.Diag("[CONN] connect attempt " + attempt);
                // TA-16b/B1+B2 — report into the shared proxy state. This loop is UNBOUNDED by design, so
                // ProxyStatus decides "failed" on WALL CLOCK, not on this attempt counter (see its remarks:
                // one visible attempt can hide a minute of invisible WTC retries). Nothing here changes the
                // retry behaviour — FAILED is display only.
                if (attempt == 1) ProxyStatus.Reset();   // a cold start: read the configured transport before the first word
                ProxyStatus.NoteAttempt();

                var loginTask = _service.LoginAsync(silentResume: true);   // silent: stored phone, no code/password block; MAY hang
                var waiter = System.Threading.Tasks.Task.Delay(TelegramService.ConnectAttemptTimeoutMs, token);
                System.Threading.Tasks.Task finished;
                try { finished = await System.Threading.Tasks.Task.WhenAny(loginTask, waiter); }
                catch { finished = null; }

                if (finished == loginTask)
                {
                    Exception failure = null;
                    try { await loginTask; } catch (Exception ex) { failure = ex; }   // observe success/exception
                    if (_service.IsAuthorized)
                    {
                        PerfLog.Boot("LoginAsync returned AUTHORIZED (attempt " + attempt + ")");
                        try { var c = _service.Client; if (c != null && c.TLConfig != null) ProxyStatus.MainDc = c.TLConfig.this_dc; } catch { }
                        ProxyStatus.NoteAuthorized();   // the ONLY evidence a proxy actually works (TA-15/X4)
                        return true;
                    }   // connected + authorized

                    if (failure != null)
                    {
                        bool corrupt = IsCorruptSessionError(failure);
                        bool needsLogin = IsNeedsLoginError(failure) || _service.NeedsInteractiveLogin || IsAuthError(failure);
                        Logger.Diag("[CONN] attempt " + attempt + " failed: " + failure.Message
                            + (corrupt ? "  [SESSION unreadable → recover]"
                               : needsLogin ? "  [no usable session/phone → stop; caller recovers]"
                               : "  [hard failure " + (hardFailures + 1) + "/" + maxHardFailures + "]"));
                        if (corrupt) { _connectCorrupt = true; return false; }        // caller deletes the dead session + recovers
                        if (needsLogin) return false;                                 // no session/phone → LoginForm / next account (NOT retried)

                        // A transport-level failure. Corrupt-session and needs-login are deliberately NOT
                        // counted above: both mean we got far enough to learn something about the ACCOUNT,
                        // which says nothing about the proxy.
                        ProxyStatus.NoteAttemptFailed();

                        // A non-network failure (e.g. the session file is locked by a lingering handle): the SAME
                        // client keeps failing, so DISCARD it (release the file → fresh client next attempt) and CAP
                        // the retries so a locked/unusable account can never loop forever (the repaint-storm bug).
                        hardFailures++;
                        await _service.DiscardFaultedClientAsync();
                        if (hardFailures >= maxHardFailures)
                        { Logger.Diag("[CONN] gave up after " + hardFailures + " non-network failures → caller recovers"); return false; }
                    }
                    else
                    {
                        if (_service.NeedsInteractiveLogin) return false;             // returned w/o auth, needs interactive login
                        hardFailures++;                                              // returned w/o auth AND w/o throwing (rare) → cap it
                        if (hardFailures >= maxHardFailures) return false;
                    }
                    // fall through to the backoff (a brief transient — e.g. a lock that may clear next attempt)
                }
                else
                {
                    bool userRetry = token.IsCancellationRequested;
                    Logger.Diag("[CONN] attempt " + attempt + (userRetry
                        ? " interrupted by Retry-now → tearing down hung attempt"
                        : " timed out after " + (TelegramService.ConnectAttemptTimeoutMs / 1000) + "s → tearing down hung attempt"));
                    await _service.TeardownHungConnectAsync();
                    SwallowFault(loginTask);   // it will fault once the socket is reset; don't let it surface
                    // A hung attempt is the CLASSIC dead-proxy signature: the TCP connect to the proxy
                    // succeeds (or stalls) and nothing ever comes back, so this branch — not the faulted
                    // one — is what a wrong secret usually looks like. A user-requested retry is not a
                    // failure and must not count toward the threshold.
                    if (!userRetry) ProxyStatus.NoteAttemptFailed();
                    if (userRetry) continue;   // Retry-now → immediate fresh attempt (skip backoff)
                }

                int secs = Math.Max(1, backoffMs / 1000);
                SetConnectingDetail("Waiting for network — make sure your VPN is on.\nRetrying in " + secs + "s… (or tap Retry now)");
                Logger.Diag("[CONN] backoff " + secs + "s before next attempt");
                try { await System.Threading.Tasks.Task.Delay(backoffMs, token); }
                catch (OperationCanceledException) { Logger.Diag("[CONN] Retry-now → retrying immediately"); }
                backoffMs = Math.Min(backoffMs * 2, TelegramService.ConnectMaxBackoffMs);
            }
        }

        /// <summary>Post-connect setup shared by resume + switch: updates, dialogs/chat list, manager seed, watchdog.</summary>
        private async System.Threading.Tasks.Task AfterConnectAsync(bool rebind = false)
        {
            _recoveryTried.Clear();   // a clean connect → reset the corrupt-recovery chain for any future corruption
            _recoveryRetried.Clear(); // …and the clean-retry gate, so a LATER corruption episode gets its own retry (ACCOUNT-RECOVERY-SAFETY)
            _service.AccountId = AccountContext.ActiveId;   // MULTI-ACCOUNT (3b): bind the active service to its id → the
                                                            // UM router forwards its updates to the UI; paths stay identical
                                                            // (AccountDir(ActiveId) == the static active path).
            // INCREMENT 3b: on a REBIND to an already-warm service its UpdateManager is ALREADY attached (routed) and
            // seeded — re-attaching/re-seeding would double-hook. Everything below still runs on the now-live target.
            if (!rebind) SubscribeUpdates();
            await LoadDialogsAsync();
            // WARMUP-FIX EARLY: kick the warm-ups HERE — the moment the active account's dialogs render (UI usable) —
            // instead of at the very END of this method (after seed/backfill/notify). That closes the ~10s "nothing
            // warming yet" window: a switch in the first seconds now finds the target already in _warming → wait-then-
            // rebind, not cold. Fire-and-forget → runs CONCURRENTLY with the active account's remaining startup (seed/
            // backfill below); the 400ms warm stagger keeps the overlapping warm connects from hammering RT. Warmed
            // ONCE (the old end-of-method call is removed). Idempotent + gated by WarmConnections.
            var __ = WarmOthersAsync();
            if (!rebind) { await _service.SeedUpdateManagerAsync(); PerfLog.Boot("SeedUpdateManagerAsync RETURNED (full dialog sweep done)"); }   // seed the manager's baseline — REQUIRED for live updates
            // NOTIFY-FIX: persist the freshly-seeded update state NOW. Otherwise the state file only updates at clean
            // exit/switch, and a crash-restart re-attaches from a stale pts → getDifference replays the whole previous
            // session's tail through the notify gate.
            // SAVESTATE-DEADLOCK: MUST be Task.Run — this continuation is inlined on the UI thread INSIDE WTC's
            // GetDifference/LoadDialogs seed (which holds the update-state semaphore); a direct SaveUpdateState() here
            // calls get_State() → Wait on that same held semaphore → UI-thread self-deadlock (HangWatch 5s). Off the UI
            // thread, the pool thread waits for the lock to free once the seed finishes, then persists. Fire-and-forget.
            var _ = System.Threading.Tasks.Task.Run(() => _service.SaveUpdateState());   // fire-and-forget (discard suppresses CS4014)
            await FetchNotifyDefaultsAsync();          // category mute defaults (users/chats/broadcasts), per connect
            // AVATAR-PIPELINE 2.1: backfill every dialog peer whose (peer, photo_id) isn't on disk — in list
            // order (visible rows first, then top-to-bottom); on-demand requests always jump ahead of this.
            _avatars.EnqueueBackfill(_allChats.OrderByDescending(c => c.Date)
                .Select(c => new KeyValuePair<long, IPeerInfo>(c.PeerId, c.PeerInfo)));
            _service.ReconnectingChanged -= OnReconnectingChanged;
            _service.ReconnectingChanged += OnReconnectingChanged;
            _service.StartConnectionWatchdog();        // detect black-holed VPN drops + force reconnect
            if (_service.Me != null)
            {
                AccountStore.WriteMeta(AccountContext.ActiveId, DisplayName(_service.Me));   // keep the switcher name fresh
                AccountStore.StampActive(AccountContext.ActiveId);   // WARMUP-FIX 1.2: mark recency → next startup warms this account first
                // Self-heal: every connected account MUST have its phone persisted (silent resume/switch reads
                // it; a missing phone makes the resume need interactive login → a spurious "logged out").
                if (!File.Exists(AccountContext.PhonePath) && !string.IsNullOrEmpty(_service.Me.phone))
                    _service.SavePhone(_service.Me.phone.StartsWith("+") ? _service.Me.phone : "+" + _service.Me.phone);
            }
            // (WARMUP-FIX EARLY: warm-ups were kicked right after LoadDialogsAsync above — NOT here — so they start ~2s
            // sooner and an early switch can wait-then-rebind. Warmed once, earlier. This is still where the active
            // account's own startup completes; re-warming the just-left account happens via that earlier kick too.)
            LoadStoriesAsync();   // STORIES-BUILD-1: fetch the story tray (fire-and-forget; tray hidden if none). Runs on cold connect AND rebind.
            PerfLog.Boot("AfterConnectAsync RETURNED (rebind=" + rebind + ") — startup complete");
        }

        /// <summary>Keys a just-resumed temp session to accounts/{Me.id}/: dispose (unlock the temp session),
        /// move session(+cache for legacy) into the per-account layout, then re-resume there. fromLegacy moves
        /// the flat legacy files + Cache/; otherwise the accounts/_pending/ files. Returns false if the
        /// re-resume needs an interactive login.</summary>
        private async System.Threading.Tasks.Task<bool> FinalizeNewAccountAsync(bool fromLegacy)
        {
            long id = _service.Me.id;
            string name = DisplayName(_service.Me);
            System.Diagnostics.Debug.WriteLine("[ACCT] finalize new account → accounts/" + id + " (legacy=" + fromLegacy + ")");
            await _service.TeardownForSwitchAsync();                       // dispose client FIRST → release the temp session lock
            if (fromLegacy) AccountStore.RelocateLegacyToAccount(id, name);   // + moves Cache/ → Cache/{id}
            else AccountStore.RelocatePendingToAccount(id, name);
            AccountContext.ActiveId = id;
            AccountContext.LegacyMode = false;
            // Guard against a bad relocate (a 0-byte/missing session) BEFORE connecting — otherwise a new client
            // would create a fresh empty session at that path and we'd loop on "buffer is null". Route to recovery.
            if (!AccountStore.SessionLooksValid(id))
            {
                System.Diagnostics.Debug.WriteLine("[SESSION] relocated session for " + id + " is missing/0-byte → recovering instead of connecting");
                _connectCorrupt = true;
                return false;
            }
            return await ConnectResilientlyAsync("Finishing account setup…");   // re-resume on accounts/{id}/session
        }

        /// <summary>Switches the active account: teardown (keep session) → clear all per-account state →
        /// repoint the cache root → resilient reconnect on the target → rebuild the UI.</summary>
        // ── WARM CONNECTIONS (ACCOUNT-SWITCH STEP 1): background clients for non-active accounts ──
        private readonly Dictionary<long, TelegramService> _warm = new Dictionary<long, TelegramService>();
        // WARMUP-FIX: in-flight warm-ups (id → the WarmOneAsync task) so a switch to a MID-warm-up account can WAIT for
        // THIS task then REBIND instead of going cold. An entry is present only while warming; on completion it moves
        // into _warm (success) and is removed here. All access is on the UI thread (no locking needed).
        private readonly Dictionary<long, System.Threading.Tasks.Task<TelegramService>> _warming = new Dictionary<long, System.Threading.Tasks.Task<TelegramService>>();
        private int _switchGen;                        // WARMUP-FIX B: bumped per switch; a warm-up wait bails if a newer switch supersedes it
        private const int WarmStartStaggerMs = 400;    // Fix A: gap between warm-up STARTS (overlap them; don't hammer connect at once)
        // Fix B: max wait for a mid-warm-up target before falling back to cold. Must OUTLAST a real warm-up — RT shows
        // ~7-10s (a WTC-internal first-connect retry, "connect attempt 2", + the UM seed), so 5s undershot and timed out
        // ~2-3s before readiness → cold. 12s covers it with margin (a mid-warm-up switch now almost always rebinds). A
        // FAILING warm-up completes early (its task finishes → WhenAny releases), so a longer cap only extends the wait
        // for accounts genuinely still warming — never hurts. Passive/responsive wait; tune here if RT warm-ups run longer.
        private const int WarmupWaitMs = 12000;

        /// <summary>INCREMENT 3b + WARMUP-FIX A: spins up a full warm background SERVICE for every NON-active account with
        /// a session — connected + a ROUTED UpdateManager (maintains live pts, silent while non-active) — so a later switch
        /// REBINDS to it instantly. Fix A: warms the MOST-RECENTLY-USED accounts FIRST (likeliest switch targets ready
        /// soonest) and OVERLAPS the warm-ups (a small stagger between STARTS, not between completions) → all accounts warm
        /// in ~one warm-up instead of N×. Idempotent (skips active/already-warm/already-warming); gated by WarmConnections.</summary>
        private async System.Threading.Tasks.Task WarmOthersAsync()
        {
            if (!AppSettings.Instance.WarmConnections) return;
            var ids = AccountStore.ListAccounts()
                .Where(a => a.Id != 0 && a.Id != AccountContext.ActiveId && !_warm.ContainsKey(a.Id) && !_warming.ContainsKey(a.Id) && AccountStore.Exists(a.Id))
                .OrderByDescending(a => a.LastActiveTicks)   // Fix A 1.2: warm the last-used account first
                .Select(a => a.Id).ToList();
            foreach (var id in ids)
            {
                if (IsDisposed) return;
                if (id == AccountContext.ActiveId || _warm.ContainsKey(id) || _warming.ContainsKey(id)) continue;   // re-check
                _warming[id] = WarmOneAsync(id);   // START it (do NOT await completion) → the warm-ups overlap
                System.Diagnostics.Debug.WriteLine("[WARMCONN] warming started id=" + id);
                await System.Threading.Tasks.Task.Delay(WarmStartStaggerMs);   // small stagger between STARTS only
            }
        }

        /// <summary>Warms ONE account into a resident service, registering it in _warm on success. Tracked in _warming
        /// while in flight (Fix B: a switch to this mid-warm-up account awaits THIS task, then rebinds). Removes itself
        /// from _warming on completion. Never throws (fire-and-forget).</summary>
        private async System.Threading.Tasks.Task<TelegramService> WarmOneAsync(long id)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var svc = await TelegramService.CreateWarmServiceAsync(id, RouteUpdate);
                if (IsDisposed) { if (svc != null) { try { await svc.DisposeWarmServiceAsync(); } catch { } } return null; }
                if (svc != null && id != AccountContext.ActiveId && !_warm.ContainsKey(id))
                {
                    _warm[id] = svc;
                    Logger.Diag("[WARMCONN] warm id=" + id + " READY in " + sw.ElapsedMilliseconds + "ms");   // tune WarmupWaitMs against this
                    return svc;
                }
                // ACCOUNT-RECOVERY-SAFETY (Bug 1, FOURTH drop site — was still the OLD synchronous path).
                // This branch fires when the warm target BECAME THE ACTIVE ACCOUNT mid-warm-up, i.e. exactly when
                // the active service is opening — or is about to open — the SAME accounts/{id}/session file. The
                // sync DisposeWarmService calls Client.Dispose() with no socket-abort and does NOT await handle
                // release, which is the documented recipe for AUTH_KEY_DUPLICATED / a mid-write corruption. The
                // three switch drop sites were fixed in v1.1; this one was missed.
                // ⚠ SHORTENED, NOT CLOSED: CreateWarmServiceAsync never re-checks ActiveId, so the warm client has
                // already held the session for the whole warm-up by the time we get here. Awaiting the teardown
                // ends the overlap promptly; it does not prevent the overlap. Detector = two [SESSPATH] lines
                // (client-open / warm-open) carrying the SAME session path. Closing it needs an ActiveId re-check
                // or a cancellation token inside CreateWarmServiceAsync — deliberately OUT OF SCOPE here.
                if (svc != null) { try { await svc.DisposeWarmServiceAsync(); } catch { } }   // became active mid-warm, or already warm → drop the extra
                return null;
            }
            catch (Exception ex) { Logger.Diag("[WARMCONN] warm-one id=" + id + " error: " + ex.Message); return null; }
            finally { _warming.Remove(id); }
        }

        /// <summary>Removes + returns the warm service for an id (ownership transfers to the caller), or null.</summary>
        private TelegramService TakeWarm(long id)
        {
            TelegramService svc;
            if (_warm.TryGetValue(id, out svc)) { _warm.Remove(id); return svc; }
            return null;
        }

        /// <summary>Disposes + clears all warm background services (app exit, or around an account-list mutation).</summary>
        private void DisposeAllWarm()
        {
            foreach (var kv in _warm) { try { kv.Value.DisposeWarmService(); } catch { } }
            _warm.Clear();
        }

        /// <summary>INCREMENT 3b: the INSTANT switch — rebind the UI to an already-warm target service (no teardown, no
        /// reconnect, no handshake). The outgoing active service is PARKED into the warm pool (stays connected + live; its
        /// routed UM self-silences the instant ActiveId flips). The target's UM (already attached + seeded) starts firing
        /// the UI immediately. A fast dialog load on the already-live connection refreshes the list (tier-2; a later
        /// refinement renders straight from cached dialogs for a zero-fetch switch).</summary>
        private async System.Threading.Tasks.Task RebindToWarmAsync(TelegramService ws)
        {
            var outgoing = _activeService;
            // Only PARK the outgoing if it's still a live account. LogOut() detaches the active client (Client=null →
            // IsAuthorized false) + deletes its session BEFORE switching to a remaining account; parking that would
            // zombie the pool (a dead, session-deleted service). A normal switch keeps the outgoing authorized → park it.
            bool parkOutgoing = outgoing != null && outgoing.IsAuthorized;
            // Detach the outgoing (going background): stop its downloads + watchdog + reconnect UI hook. Its routed UM keeps
            // running but self-silences (its id != the new ActiveId). Do NOT tear it down — staying warm is the whole point.
            try { outgoing.CancelAllDownloads("account-switch-background"); } catch { }
            try { outgoing.StopConnectionWatchdog(); } catch { }
            outgoing.ReconnectingChanged -= OnReconnectingChanged;

            // Flip the router pointer (ActiveId) FIRST, then rebind `_activeService` — with NO await between, so this whole
            // block is atomic on the UI thread and a background UM thread only ever observes the (ActiveId, _service) pair
            // as fully-outgoing or fully-target: the target's queued updates process against the target; the outgoing's
            // route silent (correct — it's now background). Any update marshalled mid-block runs (BeginInvoke) after this.
            AccountContext.ActiveId = ws.AccountId;    // flips the router: ws's UM → the UI, outgoing's UM → silent
            AccountContext.LegacyMode = false;
            _activeService = ws;                       // REBIND: every `_service` site now reads the target service
            if (parkOutgoing) _warm[outgoing.AccountId] = outgoing;   // park the outgoing as a warm background service (resident)
            AccountStore.WriteActive(ws.AccountId);
            AvatarStore.SetActive(ws.Avatars);         // secondary forms read the target account's avatars

            if (!parkOutgoing && outgoing != null) { try { outgoing.DisposeWarmService(); } catch { } }   // detached/logged-out → dispose, don't zombie
            ClearActiveAccountView();                  // REBIND-FIX: clear ONLY the outgoing's view/model — NOT the full
                                                       // per-account teardown. ResetPerAccountState's _avatars.Reset()/
                                                       // CancelAllDownloads now target the INCOMING (already-rebound)
                                                       // service, wiping the target's fresh store mid-bind → that's what
                                                       // corrupted the first switch (cold) and forced a second (warm).

            // TIER-1 seamless: paint the target's chat list INSTANTLY from its warm snapshot (no network) so there's no
            // blank/reload gap between accounts. Synchronous right after the clear (no await) → the empty frame never
            // paints. AfterConnectAsync(rebind) then refreshes from the live connection (fast; catches anything that
            // arrived while it was warm) + arms paging + backfills avatars + starts the watchdog.
            if (ws.CachedDialogs != null)
            {
                try
                {
                    _allChats.AddRange(BuildDialogEntries(ws.CachedDialogs));
                    _allChats.Sort((a, b) => b.Date.CompareTo(a.Date));
                    RenderChatList(_searchBox != null ? _searchBox.Text : "");
                }
                catch (Exception ex) { Logger.Diag("[WARMCONN] tier-1 pre-render failed: " + ex.Message); }
            }
            await AfterConnectAsync(rebind: true);     // fast dialog refresh + backfill + watchdog; SKIPS UM attach/seed (ws has both)
        }

        private async System.Threading.Tasks.Task SwitchAccountAsync(long targetId, string targetName)
        {
            if (targetId == 0 || targetId == AccountContext.ActiveId) return;
            PersistDraftForCurrentChat();   // DRAFTS: save the open chat's draft to the OUTGOING account before _service swaps
            long prevId = AccountContext.ActiveId;
            string prevName = _service.Me != null ? DisplayName(_service.Me) : null;
            Logger.Diag("[ACCT] switch start → " + targetId);
            // [SESSPATH] 0.3: the switch intent + the target's resolved session file + the CURRENT service/global ids.
            if (TelegArm.Helpers.Logger.Enabled)
                TelegArm.Helpers.Logger.Diag("[SESSPATH] switch-start from=" + prevId + " to=" + targetId
                    + " targetSession=\"" + System.IO.Path.Combine(AccountContext.AccountDir(targetId), "session") + "\""
                    + " serviceAcctId=" + _service.AccountId + " globalActiveId=" + AccountContext.ActiveId);
            // BATCH-TA-0 (A5): LogPaths was DEAD CODE — defined, never called. It adds exists= and bytes=, which the
            // switch-start line above does NOT print, and a 0-byte session is exactly what distinguishes a genuinely
            // corrupt account from a merely contended one. Wired here (target) and at recovery entry.
            AccountContext.LogPaths(targetId, "switch-start");

            int myGen = ++_switchGen;   // WARMUP-FIX B: a newer switch supersedes this one's warm-up wait / commit
            _switchInProgress = true; _switchAborted = false;

            // TIER 1 — target already warm-READY → the SEAMLESS rebind: NO overlay, NO connecting card, NO teardown. A
            // fast liveness ping runs against the CURRENT on-screen UI; on success we swap with no transition screen.
            var ws = TakeWarm(targetId);
            if (ws != null && ws.IsAuthorized && await ws.PingAliveAsync())
            {
                if (myGen != _switchGen) { _warm[targetId] = ws; return; }   // superseded during the ping → return it to the pool
                Logger.Diag("[WARMCONN] REBIND (seamless) → " + targetId + " — no overlay/teardown/handshake");
                _switchInProgress = false;
                await RebindToWarmAsync(ws);
                HideSwitchOverlay();   // clean up any overlay a superseded prior switch left showing
                Logger.Diag("[ACCT] switch done (rebind) → " + targetId);
                return;
            }
            // ACCOUNT-RECOVERY-SAFETY (Bug 1): warm but not adoptable → AWAIT its full teardown (socket reset + dispose +
            // handle release) BEFORE the cold path reopens the SAME session file, or two clients race one session →
            // AUTH_KEY_DUPLICATED / corruption (what auto-deleted account A). Sync DisposeWarmService did NOT await release.
            if (ws != null) { try { await ws.DisposeWarmServiceAsync(); } catch { } }

            // TIER 2 — WARMUP-FIX B: target is MID-warm-up → WAIT briefly for THAT warm-up to finish, then REBIND. No cold
            // teardown, no connecting card — just the calm switch overlay while it lands. Only a not-warming target or a
            // timeout falls through to the cold path. Passive wait (await the warm task) — it never triggers a teardown.
            System.Threading.Tasks.Task<TelegramService> warming;
            if (_warming.TryGetValue(targetId, out warming) && warming != null)
            {
                Logger.Diag("[WARMCONN] target " + targetId + " mid-warm-up → wait ≤" + WarmupWaitMs + "ms, then rebind");
                ShowSwitchOverlay(targetId, targetName);
                await System.Threading.Tasks.Task.WhenAny(warming, System.Threading.Tasks.Task.Delay(WarmupWaitMs));
                if (myGen != _switchGen) return;   // user switched again → the newer switch owns the overlay + state
                var ws2 = TakeWarm(targetId);      // the warm task adds to _warm on success; null if it failed or isn't done yet
                if (ws2 != null && ws2.IsAuthorized && await ws2.PingAliveAsync())
                {
                    if (myGen != _switchGen) { _warm[targetId] = ws2; return; }
                    Logger.Diag("[WARMCONN] REBIND (after warm-up wait) → " + targetId);
                    _switchInProgress = false;
                    await RebindToWarmAsync(ws2);
                    HideSwitchOverlay();
                    Logger.Diag("[ACCT] switch done (rebind-after-wait) → " + targetId);
                    return;
                }
                if (ws2 != null) { try { await ws2.DisposeWarmServiceAsync(); } catch { } }   // AWAIT release before cold reopen (Bug 1)
                Logger.Diag("[WARMCONN] warm-up wait didn't land for " + targetId + " → cold");
                // overlay stays up; the cold path below reuses it (idempotent) + adds the connecting card
            }

            // TIER 3 — COLD PATH (target not warm, not warming, or the wait didn't land): NOW show the overlay + connect
            // card to mask the slow teardown → handshake → reload.
            if (_abortConnect != null) { try { _abortConnect.Dispose(); } catch { } }
            _abortConnect = new System.Threading.CancellationTokenSource();
            ShowSwitchOverlay(targetId, targetName);   // calm transition masks teardown→connect→reload
            ShowConnecting("Switching to " + (targetName ?? "account") + "…");

            await _service.TeardownForSwitchAsync();
            ResetPerAccountState();
            AccountContext.ActiveId = targetId;     // repoints session path AND the cache root (Cache/{id})
            _service.AccountId = targetId;          // MULTI-ACCOUNT (3b): the reused service now resolves the TARGET's paths
            // WARMUP-FIX B (race guard): a mid-warm-up target could have finished DURING the teardown above and landed
            // in _warm. Now that we're cold-connecting the ACTIVE service to it, drop that redundant warm service —
            // two clients on one session = AUTH_KEY_DUPLICATED. (After ActiveId=targetId, WarmOneAsync self-drops any
            // later completion via its `id != ActiveId` check, so this closes the only window.)
            var staleWarm = TakeWarm(targetId);
            if (staleWarm != null) { try { await staleWarm.DisposeWarmServiceAsync(); } catch { } }   // AWAIT release before cold reopen (Bug 1)
            AccountContext.LegacyMode = false;
            AccountStore.WriteActive(targetId);

            // ACCOUNT-RECOVERY-SAFETY (Bug 1): final gate — do NOT cold-open the target session until its file is actually
            // UNLOCKED (any just-dropped warm client's handle fully released), so two clients never touch one session file.
            await AccountStore.WaitSessionUnlockedAsync(targetId);
            bool connected = await ConnectResilientlyAsync("Switching to " + (targetName ?? "account") + "…");
            _switchInProgress = false;

            if (!connected && _switchAborted && prevId != 0)
            {
                // Cancel → restore the previously-active account (it's still valid; just reconnect on it).
                Logger.Diag("[ACCT] switch cancelled → restoring active=" + prevId);
                _abortConnect = null;                 // the restore must NOT auto-abort
                AccountContext.ActiveId = prevId;
                _service.AccountId = prevId;           // MULTI-ACCOUNT (3b): restore → resolve the previous account's paths
                AccountStore.WriteActive(prevId);
                ShowSwitchOverlay(prevId, prevName);
                ShowConnecting("Returning to " + (prevName ?? "your account") + "…");
                bool back = await ConnectResilientlyAsync("Returning to " + (prevName ?? "your account") + "…");
                if (!back) { HideSwitchOverlay(); await OnConnectFailedAsync(); return; }
                HideConnecting();
                await AfterConnectAsync();
                HideSwitchOverlay();
                return;
            }
            _abortConnect = null;
            if (!connected) { HideSwitchOverlay(); await OnConnectFailedAsync(); return; }
            HideConnecting();
            await AfterConnectAsync();
            HideSwitchOverlay();
            Logger.Diag("[ACCT] switch done (cold) → " + targetId);
        }

        /// <summary>Clears the ACTIVE account's on-screen VIEW + in-memory render model (open chat, chat list,
        /// per-chat caches) so the next account renders fresh with no cross-account mixing — and stops the previous
        /// account's audio/inline video as part of that teardown. SAFE on a seamless REBIND: it touches ONLY MainForm
        /// UI/model state, NEVER the (now already-incoming) service's client/UM/avatar store.</summary>
        private void ClearActiveAccountView()
        {
            CloseDrawer();
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] Reset: before AudioPlayer.Stop");
            try { AudioPlayer.Stop(); } catch { }
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] Reset: after AudioPlayer.Stop; before ClearMessagePanel");
            _selectedChat = null; _selectedItem = null;
            ShowColumn(true);   // one column (owner 2026-10-15): no chat open → the list. UI only.
            // ACCOUNT-RECOVERY-SAFETY (Bug 3): drop the header-avatar reference NOW. It points at a bitmap OWNED by the
            // outgoing account's avatar store, which ResetPerAccountState is about to DISPOSE — leaving a pending header
            // repaint drawing a disposed Image ("Parameter is not valid", fatal in a Paint handler). Null it → the next
            // paint shows the initials circle; SetHeaderAvatar reloads a fresh one when a chat opens.
            _headerAvatarImg = null; _headerAvatarPeerId = 0; _headerAvatarTitle = null;
            if (_chatSearchBtn != null) _chatSearchBtn.Visible = false;   // INCHAT-SEARCH: no open chat → no header magnifier
            if (_chatMenuBtn != null) _chatMenuBtn.Visible = false;       // TA-21/S1a: …and no header ⋮ (it acts on the open chat)
            if (_dockBtn != null) _dockBtn.Visible = false;               // TA-23/D1c: …nor the dock toggle
            SetDockOpen(false);                                           // an open dock with no chat has nothing to show
            ClearMessagePanel();                         // disposes message bubbles + per-chat photo caches
            System.Diagnostics.Debug.WriteLine("[LOGOUT-TRACE] Reset: after ClearMessagePanel");
            _currentChatMessages.Clear();
            foreach (Control c in _chatListPanel.Controls.OfType<Control>().ToArray()) { _chatListPanel.Controls.Remove(c); c.Dispose(); }
            _allChats.Clear();
            _shownMessageIds.Clear();
            _albumBubbles.Clear();
            _peerNames.Clear();
            foreach (var img in _customEmojiCache.Values) { try { img.Dispose(); } catch { } }
            _customEmojiCache.Clear();
            _photoCachePaths.Clear();
            _pinnedMessages = null; _pinnedChatId = 0;
            if (_pinnedBar != null) _pinnedBar.Visible = false;
            _jumpUnread = 0;
            _inChatSearchEntry = null;   // INCHAT-SEARCH: any scoped search belongs to the outgoing account
            // STORIES: the tray is per-account — drop the old account's cache token + chips (the incoming account
            // refetches on its AfterConnectAsync, so the tray never shows the previous account's stories).
            _storiesState = null;
            _storyPeers = new List<StoryTrayEntry>();
            if (_storyTrayBar != null && !_storyTrayBar.IsDisposed)
            {
                foreach (var c in _storyTrayBar.Controls.Cast<Control>().ToArray()) c.Dispose();
                _storyTrayBar.Controls.Clear();
            }
            ShowStoryTray(false);
        }

        /// <summary>FULL per-account teardown for the COLD / logout / add-account switch (teardown-and-rebuild model):
        /// the view clear PLUS disposing the OUTGOING account's transfers + avatar store. Correct ONLY where
        /// <c>_service</c>/<c>_avatars</c> are still the account being LEFT (after TeardownForSwitchAsync / logout).
        /// A seamless REBIND must NOT call this — REBIND-FIX: there <c>_service</c>/<c>_avatars</c> are already the
        /// INCOMING target, per-instance avatar stores never bleed (increment 1), so <c>_avatars.Reset()</c> would
        /// wipe the target's fresh store mid-bind (it corrupted the first switch → the "needs two switches" bug).</summary>
        private void ResetPerAccountState()
        {
            ClearActiveAccountView();
            // ACCOUNT teardown is the ONE place background downloads must die: a cross-account transfer writing into a
            // switched cache root would violate isolation (DOWNLOAD-UX 2.1 invariant).
            try { _service.CancelAllDownloads("account-switch"); } catch { }
            _avatars.Reset();   // account-scoped ids + disk root → clear the OUTGOING store (disposes cached bitmaps)
            System.Diagnostics.Debug.WriteLine("[ACCT] per-account in-memory state reset");
        }

        /// <summary>Shows/clears a "Reconnecting…" hint in the window title while the watchdog rebuilds the link.</summary>
        private void OnReconnectingChanged(bool reconnecting)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (reconnecting) { if (Text != ReconnectingTitle) { _titleBeforeReconnect = Text; Text = ReconnectingTitle; } }
                    else if (_titleBeforeReconnect != null) { Text = _titleBeforeReconnect; _titleBeforeReconnect = null; }

                    // A MID-SESSION DROP had no visible signal beyond this window title — easy to miss,
                    // and on a direct connection the pill was hidden entirely. Drive the shared state so
                    // the pill appears and says "Connecting…" while the link is being re-established.
                    // (Recovery is reported by NoteActivity when traffic resumes — whoever re-established
                    // it, including WTC's own reactor, which never routes through our connect loop.)
                    if (reconnecting) ProxyStatus.NoteAttempt();
                    RefreshProxyPill();
                }));
            }
            catch { /* form went away */ }
        }

        /// <summary>Observes a faulted/abandoned Task so its exception can't surface as Unobserved (a hung
        /// connect we tore down will fault once the socket is reset).</summary>
        private static void SwallowFault(System.Threading.Tasks.Task t)
        {
            if (t == null) return;
            t.ContinueWith(x => { var ignore = x.Exception; },
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
                | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>True for RPC errors that mean the session is no longer valid.</summary>
        private static bool IsAuthError(Exception ex)
        {
            var rpc = (ex as RpcException) ?? (ex.InnerException as RpcException);
            if (rpc == null) return false;
            var msg = (rpc.Message ?? "").ToUpperInvariant();
            return msg.Contains("AUTH") || msg.Contains("SESSION") || msg.Contains("USER_DEACTIVATED");
        }

        /// <summary>A PERMANENT, unrecoverable session-file error (corrupt/empty/truncated): WTelegram throws
        /// "Exception while reading session file: Value cannot be null. Parameter name: buffer / Use the correct
        /// api_hash/id/key, or delete the file to start a new session". Retrying NEVER fixes it — the file must
        /// be deleted. Distinct from a network drop (transient) or an RPC auth error (re-login).</summary>
        private static bool IsCorruptSessionError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message ?? "";
                if (m.IndexOf("session file", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (m.IndexOf("delete the file to start a new session", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (m.IndexOf("correct api_hash", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>A silent resume that can't proceed without interactive login: WTelegram throws "You must
        /// provide a config value for phone_number" (no stored phone / unreadable session). PERMANENT for a
        /// silent resume — retrying just re-throws it (the infinite "config value for phone_number" loop). The
        /// account is unusable as-is → caller drops to LoginForm / the next valid account.</summary>
        private static bool IsNeedsLoginError(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message ?? "";
                if (m.IndexOf("provide a config value", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>Shared handler for a failed connect: a corrupt session is deleted + recovered (never a forever
        /// loop); a genuine auth failure drops to the LoginForm.</summary>
        private async System.Threading.Tasks.Task OnConnectFailedAsync()
        {
            HideConnecting();
            if (_connectCorrupt)
            {
                _connectCorrupt = false;
                await RecoverCorruptSessionAsync();
            }
            else FallBackToLogin();
        }

        /// <summary>ACCOUNT-RECOVERY-SAFETY: the active account's session came back "unreadable". This NEVER auto-deletes the
        /// account (that turned a transient switch-race corruption into permanent loss). Instead: (1) a CLEAN RETRY — release
        /// every handle on the session file and reconnect ONCE (a warm/cold-race contention usually clears here → the account
        /// is KEPT); (2) only if it's STILL corrupt, MOVE THE SESSION ASIDE (rename to accounts/{id}.corrupt-&lt;ts&gt;, preserved
        /// + recoverable) and fall back to another account / the LoginForm. A non-corrupt (network) retry failure leaves the
        /// account untouched. Bounded (one retry + one move-aside per account) → never loops.</summary>
        private async System.Threading.Tasks.Task RecoverCorruptSessionAsync()
        {
            long badId = AccountContext.ActiveId;
            bool legacy = AccountContext.LegacyMode || badId == 0;
            bool stillCorrupt = true;   // we were invoked because a corrupt signal fired; the retry may clear it
            // BATCH-TA-0 (A5): exists= / bytes= at recovery entry — a 0-byte session is the signature of the
            // truncating two-client race, and nothing else in the [RECOVERY] trace reports the file's size.
            AccountContext.LogPaths(badId, "recover-entry");

            // (1) CLEAN RETRY (once per account, real accounts only). A "corrupt" connect is USUALLY transient contention —
            // a just-dropped warm client's handle still releasing, or a mid-write from the switch race — not permanent rot.
            // Release EVERY handle on the session file, confirm it's unlocked, then reconnect once. If it connects, the
            // account is KEPT and NOTHING is deleted or moved.
            if (!legacy && badId != 0 && _recoveryRetried.Add(badId))
            {
                LogRecovery("session corrupt id=" + badId + " → clean-retry (release all handles + reconnect once, NO delete)");
                await _service.DiscardFaultedClientAsync();                 // dispose the faulted client + wait for its file lock to release
                var warmOnBad = TakeWarm(badId);                            // drop any warm client STILL on this session (the race source)
                if (warmOnBad != null) { try { await warmOnBad.DisposeWarmServiceAsync(); } catch { } }
                await AccountStore.WaitSessionUnlockedAsync(badId);         // confirm the file is truly free before reopening
                _connectCorrupt = false;
                if (await ConnectResilientlyAsync("Reconnecting…"))
                {
                    LogRecovery("clean-retry SUCCEEDED id=" + badId + " → account KEPT (corruption was transient; NOT deleted)");
                    HideConnecting();
                    await AfterConnectAsync();
                    return;
                }
                stillCorrupt = _connectCorrupt;   // true → genuinely unreadable (move aside); false → network/other (leave in place)
                LogRecovery("clean-retry did not connect id=" + badId + " stillCorrupt=" + stillCorrupt);
            }

            // (2) FALL BACK — never delete. Move the session ASIDE only if it's still genuinely corrupt (preserved, recoverable);
            // a non-corrupt (network) failure leaves the account's files untouched so it recovers when the connection returns.
            _recoveryTried.Add(badId);
            HideConnecting();
            await _service.TeardownForSwitchAsync();   // release the file lock BEFORE any rename
            if (stillCorrupt)
            {
                string moved = legacy ? AccountStore.MoveLegacySessionAside()
                                      : (badId != 0 ? AccountStore.MoveAccountDirAside(badId) : null);
                LogRecovery("session still unreadable id=" + badId + " → MOVED ASIDE to \"" + (moved ?? "(left in place)") + "\" (PRESERVED, NOT deleted)");
            }
            else LogRecovery("retry failed but NOT corrupt id=" + badId + " → account left in place (no move-aside), falling back");
            _connectCorrupt = false;
            AccountContext.LegacyMode = false;
            AuthManager.Reset();

            // Pick a VALID candidate NOT already tried in this chain and NOT mid-background-delete — so we can't
            // cycle the same broken accounts forever (the bound), nor race the logout cleanup on a deleting account.
            var others = AccountStore.ListAccounts()
                .FindAll(a => a.Id != badId && !_recoveryTried.Contains(a.Id) && !AccountStore.IsDeleting(a.Id));
            if (others.Count > 0)
            {
                LogRecovery("falling back → switching to " + others[0].Id);
                AccountContext.ActiveId = 0;
                AccountStore.WriteActive(others[0].Id);
                await SwitchAccountAsync(others[0].Id, others[0].Name);   // if it's ALSO corrupt → recurses, but each id is tried once → terminates
            }
            else
            {
                LogRecovery("no untried valid account left → LoginForm");
                _recoveryTried.Clear();
                _recoveryRetried.Clear();
                AccountContext.ActiveId = 0;
                AccountStore.WriteActive(0);
                ShowLoginForm();
            }
        }

        private void FallBackToLogin()
        {
            if (_fellBack) return;
            _service.Dispose();
            AuthManager.Reset();
            ShowLoginForm();
        }

        /// <summary>Hides MainForm and shows a fresh LoginForm; closing it ends the app.</summary>
        private void ShowLoginForm()
        {
            if (_fellBack) return;
            _fellBack = true;

            if (_notifyIcon != null) _notifyIcon.Visible = false; // no tray while on the login screen
            // TA-27: and no notification windows either — clicking one after a logout would try to open a
            // chat in an account that is gone. ⚠ Deliberately NOT done in SwitchAccountAsync: a window for
            // a still-logged-in account stays VALID across a switch, and OpenNotifiedChat already switches
            // back for it. Touching the switch path for a cosmetic tidy-up is not worth entering that code
            // (CLAUDE.md danger zone / HANDOFF §5.10-5.11).
            try { NotificationStack.CloseAll(); } catch { }

            // ACCOUNT-SESSION-PATH-FIX: a fresh login must NOT inherit a prior account's id, or Config("session_pathname")
            // resolves to accounts/{staleId}/session instead of _pending (the same collision as the add-account bug). Every
            // caller means "no active account → log in fresh" (logout-last, all-corrupt recovery, add-cancel-first,
            // connect-failure fallback), so 0 — the first-launch state — is always correct here.
            _service.AccountId = 0;

            var login = new LoginForm(_service);
            login.FormClosed += (s, e) => { _reallyClosing = true; Close(); };
            Hide();
            login.Show();
        }
    }
}
