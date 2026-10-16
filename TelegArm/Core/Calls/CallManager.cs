using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using TelegArm.Helpers;
using TL;

namespace TelegArm.Core.Calls
{
    public enum CallPhase { Requesting, Waiting, Ringing, Incoming, Accepting, Connecting, Established, Reconnecting, Ended }

    /// <summary>One 1:1 call, as the UI sees it. Changed only on the UI thread (CallManager marshals everything there).</summary>
    public sealed class CallSession
    {
        public long Id { get; internal set; }
        public long AccessHash { get; internal set; }
        public User Peer { get; internal set; }
        public long PeerId { get; internal set; }
        public bool Outgoing { get; internal set; }
        public bool Video { get; internal set; }
        public CallPhase Phase { get; internal set; }
        public string EndText { get; internal set; }
        public DateTime? EstablishedUtc { get; internal set; }
        public int SignalBars { get; internal set; } = -1;
        public bool Muted { get; internal set; }
        public bool RemoteMuted { get; internal set; }
        /// <summary>Our camera is wanted on: sent once the call runs (an incoming video call starts with it on, as
        /// Telegram Desktop's does; Accept then opens it).</summary>
        public bool CameraOn { get; internal set; }
        /// <summary>The camera is opening or switching.</summary>
        public bool CameraBusy { get; internal set; }
        /// <summary>The other side's video: 0 off, 1 paused, 2 on (tgcalls' VideoState).</summary>
        public int RemoteVideo { get; internal set; }
        /// <summary>Why the camera is not on although it was asked for (no camera, busy, privacy, not connected yet).</summary>
        public string CameraNote { get; internal set; }
        public bool CanSwitchCamera { get { var c = Camera; return c != null && c.Cameras != null && c.Cameras.Count > 1; } }
        /// <summary>Our camera's picture (mirrored) and the other side's, for the call window to paint.</summary>
        public readonly CallVideoSink LocalVideo = new CallVideoSink();
        public readonly CallVideoSink RemoteFrames = new CallVideoSink();
        public bool Ended { get { return Phase == CallPhase.Ended; } }
        public int DurationSeconds { get { return EstablishedUtc == null ? 0 : (int)(DateTime.UtcNow - EstablishedUtc.Value).TotalSeconds; } }

        internal BigInteger Secret;
        internal byte[] GA, GAHash, Key;
        internal long Fingerprint;
        internal NativeCall Native;
        internal CallCamera Camera;
        internal bool CameraPending;   // opened before the call connected: handed to the instance once it does (V1 needs that)
        internal bool DiscardWhenKnown;
        internal readonly List<byte[]> EarlySignaling = new List<byte[]>();
        internal readonly SemaphoreSlim SendGate = new SemaphoreSlim(1, 1);
        internal InputPhoneCall Input { get { return new InputPhoneCall { id = Id, access_hash = AccessHash }; } }
    }

    /// <summary>
    /// Telegram 1:1 calls over MTProto (WTelegramClient's phone.* methods) and tgcalls (NativeCall), following Telegram
    /// Desktop 7.3.1's calls_call.cpp: phoneCallProtocol {udp_p2p, udp_reflector, min_layer 65, max_layer = tgcalls',
    /// library_versions = tgcalls' list reversed}; the caller sends SHA-256(g_a), the callee g_b, the caller g_a + the key
    /// fingerprint; the instance starts with the first of the server's library_versions; Telegram's relays go in both as
    /// endpoints (UDP, 16-byte tags) and as "reflector" TURN servers (id = position in the sorted set of their ids + 1,
    /// password = the tag in hex); hanging up sends Missed while it still rings out, Busy for a declined incoming call,
    /// else Hangup. One call at a time: another incoming call is declined as busy.
    /// Video (2026-10-16): phone.requestCall says "video" only when a camera actually started (tdesktop: f_video iff it
    /// has a capture); the camera goes into the instance at its start or later (turning it on in a voice call waits for
    /// the connection, as tdesktop's NotStartedCall); the remote picture and state come from tgcalls; phone.discardCall
    /// says "video" when either side sent video.
    /// Everything public runs on the UI thread, and every event is raised there.
    /// </summary>
    public sealed class CallManager
    {
        private const int MinLayer = 65;

        private readonly TelegramService _svc;
        private readonly SynchronizationContext _ui;
        private int _dhVersion;
        private Task _teardown;   // the last call's native stop + camera close (Shutdown waits for it)
        private byte[] _dhP;
        private int _dhG;

        public CallSession Current { get; private set; }
        public TelegramService Service { get { return _svc; } }

        /// <summary>Anything about <see cref="Current"/> changed (phase, bars, mute, end).</summary>
        public event Action<CallSession> Changed;
        /// <summary>A call is ringing here.</summary>
        public event Action<CallSession> Incoming;

        public CallManager(TelegramService svc, SynchronizationContext ui)
        {
            _svc = svc;
            _ui = ui;
        }

        public static bool Available { get { return TgCalls.Available; } }
        public bool Busy { get { return Current != null && !Current.Ended; } }
        /// <summary>A voice chat is on (set by the window): no 1:1 call meanwhile, and an incoming one is answered busy.</summary>
        public Func<bool> OtherBusy;
        private bool InVoiceChat { get { var f = OtherBusy; return f != null && f(); } }

        private WTelegram.Client Client { get { return _svc.Client; } }

        private void Raise(CallSession s) { var h = Changed; if (h != null) h(s); }
        private void Post(Action a) { _ui.Post(_ => { try { a(); } catch (Exception ex) { Logger.Diag("[CALL] UI handler: " + ex); } }, null); }

        private static PhoneCallProtocol Protocol()
        {
            return new PhoneCallProtocol
            {
                flags = PhoneCallProtocol.Flags.udp_p2p | PhoneCallProtocol.Flags.udp_reflector,
                min_layer = MinLayer,
                max_layer = TgCalls.MaxLayer(),
                library_versions = TgCalls.ApiVersions(),
            };
        }

        // ── DH ──────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The server's random for this exchange; p and g are checked (once per config version) and kept.</summary>
        private async Task<byte[]> DhRandomAsync()
        {
            var r = await Client.Messages_GetDhConfig(_dhVersion, 256);
            var cfg = r as Messages_DhConfig;
            if (cfg != null)
            {
                string why = null;
                bool ok = await Task.Run(() => CallCrypto.CheckPrime(cfg.p, cfg.g, 20, out why));   // the slow proof only for an unknown prime
                if (!ok) throw new Exception("The server's DH parameters are not safe (" + why + ").");
                _dhP = cfg.p; _dhG = cfg.g; _dhVersion = cfg.version;
                return cfg.random;
            }
            var nm = r as Messages_DhConfigNotModified;
            if (nm != null && _dhP != null) return nm.random;
            throw new Exception("No DH parameters from the server.");
        }

        private BigInteger P { get { return CallCrypto.FromBigEndian(_dhP); } }

        // ── outgoing ────────────────────────────────────────────────────────────────────────────────────────────────

        public async Task StartAsync(User user, bool video = false)
        {
            if (Busy) throw new InvalidOperationException("Already in a call.");
            if (InVoiceChat) throw new InvalidOperationException("Leave the voice chat first.");
            if (!TgCalls.Available) throw new InvalidOperationException("Calls are unavailable here (" + TgCalls.Status + ").");
            var s = new CallSession { Peer = user, PeerId = user.id, Outgoing = true, Video = video, CameraOn = video, Phase = CallPhase.Requesting };
            Current = s;
            Raise(s);
            try
            {
                if (video) await OpenCameraAsync(s);   // the request says "video" only with a camera that works
                if (s.Ended) return;
                s.Video = s.Camera != null;
                var random = await DhRandomAsync();
                s.Secret = CallCrypto.NewSecret(random);
                var ga = BigInteger.ModPow(new BigInteger(_dhG), s.Secret, P);
                if (!CallCrypto.CheckPublic(ga, P)) throw new Exception("g_a out of range");
                s.GA = CallCrypto.ToBigEndian256(ga);
                s.GAHash = CallCrypto.Sha256(s.GA);
                var res = await Client.Phone_RequestCall(new InputUser(user.id, user.access_hash), new Random().Next(), s.GAHash, Protocol(), s.Video);
                Logger.Diag("[CALL] requested → " + res.phone_call.GetType().Name);
                await HandleAsync(res.phone_call);
            }
            catch (Exception ex)
            {
                Logger.Diag("[CALL] start failed: " + ex.Message);
                End(s, Friendly(ex, "Couldn't start the call"));
            }
        }

        // ── incoming ────────────────────────────────────────────────────────────────────────────────────────────────

        public async Task AcceptAsync()
        {
            var s = Current;
            if (s == null || s.Outgoing || s.Phase != CallPhase.Incoming) return;
            s.Phase = CallPhase.Accepting;
            Raise(s);
            try
            {
                if (s.CameraOn) await OpenCameraAsync(s);   // a camera that will not start leaves an answered voice call
                if (s.Ended) return;
                var random = await DhRandomAsync();
                s.Secret = CallCrypto.NewSecret(random);
                var gb = BigInteger.ModPow(new BigInteger(_dhG), s.Secret, P);
                if (!CallCrypto.CheckPublic(gb, P)) throw new Exception("g_b out of range");
                var res = await Client.Phone_AcceptCall(s.Input, CallCrypto.ToBigEndian256(gb), Protocol());
                await HandleAsync(res.phone_call);
            }
            catch (Exception ex)
            {
                Logger.Diag("[CALL] accept failed: " + ex.Message);
                await DiscardAsync(s, new PhoneCallDiscardReasonHangup(), Friendly(ex, "Couldn't answer"));
            }
        }

        /// <summary>Decline (an incoming call), cancel (an outgoing one still ringing) or hang up.</summary>
        public Task HangUpAsync()
        {
            var s = Current;
            if (s == null || s.Ended) return Task.FromResult(0);
            PhoneCallDiscardReason reason;
            if (s.Outgoing && (s.Phase == CallPhase.Requesting || s.Phase == CallPhase.Waiting || s.Phase == CallPhase.Ringing)) reason = new PhoneCallDiscardReasonMissed();
            else if (!s.Outgoing && s.Phase == CallPhase.Incoming) reason = new PhoneCallDiscardReasonBusy();
            else reason = new PhoneCallDiscardReasonHangup();
            string text = !s.Outgoing && s.Phase == CallPhase.Incoming ? "Declined" : s.EstablishedUtc != null ? "Call ended" : "Cancelled";
            return DiscardAsync(s, reason, text);
        }

        public void SetMuted(bool muted)
        {
            var s = Current;
            if (s == null || s.Ended) return;
            s.Muted = muted;
            if (s.Native != null) s.Native.SetMute(muted);
            Raise(s);
        }

        // ── video ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Turns our camera on or off. On: in a ringing incoming call it only marks it (Accept opens it); during
        /// a call it opens the camera and sends it - once connected, unless the call was started as a video call.</summary>
        public async Task SetCameraAsync(bool on)
        {
            var s = Current;
            if (s == null || s.Ended || s.CameraBusy) return;
            if (!on)
            {
                s.CameraOn = false;
                s.CameraNote = null;
                s.CameraPending = false;
                var cam = s.Camera;
                s.Camera = null;
                if (s.Native != null) s.Native.SetCamera(IntPtr.Zero);
                s.CameraBusy = cam != null;   // "Start video" waits until the device is free again
                Raise(s);
                if (cam != null) await Task.Run(() => cam.Dispose());
                s.CameraBusy = false;
                s.LocalVideo.Clear();         // after the close: the preview callback is gone, no late picture refills it
                Raise(s);
                return;
            }
            if (s.Phase == CallPhase.Incoming) { s.CameraOn = true; s.CameraNote = null; Raise(s); return; }
            if (s.EstablishedUtc == null && !(s.Outgoing && s.Video))
            {
                s.CameraNote = "You can turn on video once the call is connected.";
                Raise(s);
                return;
            }
            s.CameraOn = true;
            await OpenCameraAsync(s);
            if (s.Camera == null || s.Ended) return;
            if (s.Native != null && s.EstablishedUtc != null) s.Native.SetCamera(s.Camera.Handle);
            else if (s.Native != null) s.CameraPending = true;   // still connecting: OnNativeState hands it over (V1 needs that)
        }

        /// <summary>The other camera (front and back), when there is one.</summary>
        public async Task SwitchCameraAsync()
        {
            var s = Current;
            if (s == null || s.Ended || s.CameraBusy || !s.CanSwitchCamera) return;
            s.CameraBusy = true;
            Raise(s);
            int r = await s.Camera.SwitchAsync();
            s.CameraBusy = false;
            if (r < 0) { await CameraLostAsync(s, s.Camera, "The camera stopped."); return; }
            s.CameraNote = r > 0 ? null : "The other camera didn't start.";
            Raise(s);
        }

        /// <summary>The camera stopped by itself (another app took it, unplugged) or nothing runs after a failed switch: the
        /// same as Stop video, with a note.</summary>
        private async Task CameraLostAsync(CallSession s, CallCamera cam, string why)
        {
            if (s.Ended || s.Camera != cam || cam == null) return;
            Logger.Diag("[CALL] camera lost: " + why);
            await SetCameraAsync(false);
            if (!s.Ended) { s.CameraNote = why; Raise(s); }
        }

        private async Task OpenCameraAsync(CallSession s)
        {
            if (s.Camera != null || s.CameraBusy) return;
            s.CameraBusy = true;
            s.CameraNote = null;
            s.LocalVideo.Clear();   // no old picture until the new camera's first
            Raise(s);
            CallCamera cam = null;
            string why = null;
            try { cam = await CallCamera.OpenAsync(s.LocalVideo); }
            catch (Exception ex) { why = CallCamera.Problem(ex); }
            s.CameraBusy = false;
            if (cam != null && (s.Ended || !s.CameraOn)) { var late = cam; cam = null; var t = Task.Run(() => late.Dispose()); }   // hung up meanwhile
            if (cam == null)
            {
                s.CameraOn = false;
                if (!s.Ended) s.CameraNote = why;
                if (why != null) Logger.Diag("[CALL] camera: " + why);
            }
            else
            {
                s.Camera = cam;
                s.Video = true;
                var opened = cam;
                cam.Failed += reason => Post(() => { var t = CameraLostAsync(s, opened, "The camera stopped (" + reason + ")."); });
                Logger.Diag("[CALL] camera: " + (cam.Current != null ? cam.Current.Name : "?") + " via " + cam.Backend);
            }
            Raise(s);
        }

        private async Task DiscardAsync(CallSession s, PhoneCallDiscardReason reason, string text)
        {
            int duration = s.DurationSeconds;
            bool known = s.Id != 0;
            if (!known) s.DiscardWhenKnown = true;   // still being requested: discarded as soon as the server names it
            End(s, text);
            if (!known) return;
            try { await Client.Phone_DiscardCall(s.Input, duration, reason, 0, s.Video); }
            catch (Exception ex) { Logger.Diag("[CALL] discard: " + ex.Message); }
        }

        // ── updates ─────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>From the update handler (UI thread): updatePhoneCall / updatePhoneCallSignalingData.</summary>
        public void OnUpdate(Update u)
        {
            var pc = u as UpdatePhoneCall;
            if (pc != null) { var t = HandleAsync(pc.phone_call); return; }
            var sig = u as UpdatePhoneCallSignalingData;
            if (sig != null)
            {
                var s = Current;
                if (s == null || s.Ended || s.Id != sig.phone_call_id) return;
                if (s.Native != null) s.Native.ReceiveSignaling(sig.data);
                else s.EarlySignaling.Add(sig.data);   // the peer started first: kept for our instance
            }
        }

        private bool Matches(CallSession s, long id) { return s != null && !s.Ended && (s.Id == id || (s.Id == 0 && s.Outgoing)); }

        private async Task HandleAsync(PhoneCallBase call)
        {
            try
            {
                var requested = call as PhoneCallRequested;
                if (requested != null) { await OnRequestedAsync(requested); return; }
                var waiting = call as PhoneCallWaiting;
                if (waiting != null) { OnWaiting(waiting); return; }
                var accepted = call as PhoneCallAccepted;
                if (accepted != null) { await OnAcceptedAsync(accepted); return; }
                var full = call as PhoneCall;
                if (full != null) { OnCall(full); return; }
                var discarded = call as PhoneCallDiscarded;
                if (discarded != null) { OnDiscarded(discarded); return; }
            }
            catch (Exception ex)
            {
                Logger.Diag("[CALL] handling " + (call != null ? call.GetType().Name : "null") + ": " + ex);
                var s = Current;
                if (s != null && !s.Ended) await DiscardAsync(s, new PhoneCallDiscardReasonDisconnect(), Friendly(ex, "The call failed"));
            }
        }

        private async Task OnRequestedAsync(PhoneCallRequested r)
        {
            var input = new InputPhoneCall { id = r.id, access_hash = r.access_hash };
            if (Busy || InVoiceChat)
            {
                if (Busy && Current.Id == r.id) return;
                Logger.Diag("[CALL] incoming call while in a call or voice chat → busy");
                try { await Client.Phone_DiscardCall(input, 0, new PhoneCallDiscardReasonBusy(), 0, (r.flags & PhoneCallRequested.Flags.video) != 0); } catch { }
                return;
            }
            User u = null;
            var users = _svc.Updates != null ? _svc.Updates.Users : null;
            if (users != null) users.TryGetValue(r.admin_id, out u);
            var s = new CallSession
            {
                Id = r.id, AccessHash = r.access_hash, Peer = u, PeerId = r.admin_id, Outgoing = false,
                Video = (r.flags & PhoneCallRequested.Flags.video) != 0, Phase = CallPhase.Incoming, GAHash = r.g_a_hash,
            };
            s.CameraOn = s.Video;
            Current = s;
            Logger.Diag("[CALL] incoming from " + r.admin_id + (s.Video ? " (video)" : ""));
            Raise(s);
            var h = Incoming;
            if (h != null) h(s);
            try { await Client.Phone_ReceivedCall(s.Input); } catch (Exception ex) { Logger.Diag("[CALL] receivedCall: " + ex.Message); }
        }

        private void OnWaiting(PhoneCallWaiting w)
        {
            var s = Current;
            if (!Matches(s, w.id) || !s.Outgoing)
            {
                if (s != null && s.DiscardWhenKnown && s.Id == 0) { s.Id = w.id; s.AccessHash = w.access_hash; var t = Client.Phone_DiscardCall(s.Input, 0, new PhoneCallDiscardReasonMissed(), 0, s.Video); }
                return;
            }
            s.Id = w.id;
            s.AccessHash = w.access_hash;
            s.Phase = w.receive_date != default(DateTime) ? CallPhase.Ringing : CallPhase.Waiting;
            Raise(s);
        }

        private async Task OnAcceptedAsync(PhoneCallAccepted a)
        {
            var s = Current;
            if (!Matches(s, a.id) || !s.Outgoing || s.Key != null) return;
            s.Id = a.id;
            s.AccessHash = a.access_hash;
            var gb = CallCrypto.FromBigEndian(a.g_b);
            if (!CallCrypto.CheckPublic(gb, P)) throw new Exception("g_b out of range");
            s.Key = CallCrypto.ToBigEndian256(BigInteger.ModPow(gb, s.Secret, P));
            s.Fingerprint = CallCrypto.Fingerprint(s.Key);
            s.Phase = CallPhase.Connecting;
            Raise(s);
            var res = await Client.Phone_ConfirmCall(s.Input, s.GA, s.Fingerprint, Protocol());
            await HandleAsync(res.phone_call);
        }

        private void OnCall(PhoneCall c)
        {
            var s = Current;
            if (!Matches(s, c.id) || s.Native != null) return;   // (the caller gets it twice: confirmCall's answer, then the update)
            s.Id = c.id;
            s.AccessHash = c.access_hash;
            if (!s.Outgoing)
            {
                if (s.Secret.IsZero) return;   // not accepted here (another device took it: the server discards it for us)
                if (s.GAHash == null || !CallCrypto.Sha256(c.g_a_or_b).SequenceEqual(s.GAHash)) throw new Exception("g_a does not match its hash");
                var ga = CallCrypto.FromBigEndian(c.g_a_or_b);
                if (!CallCrypto.CheckPublic(ga, P)) throw new Exception("g_a out of range");
                s.Key = CallCrypto.ToBigEndian256(BigInteger.ModPow(ga, s.Secret, P));
                s.Fingerprint = CallCrypto.Fingerprint(s.Key);
            }
            if (s.Key == null) return;
            if (c.key_fingerprint != s.Fingerprint) throw new Exception("key fingerprint mismatch");
            StartNative(s, c);
        }

        private void OnDiscarded(PhoneCallDiscarded d)
        {
            var s = Current;
            if (s == null || s.Ended || s.Id != d.id) return;
            string text;
            if (d.reason is PhoneCallDiscardReasonBusy) text = s.Outgoing ? "Busy" : "Call ended";
            else if (d.reason is PhoneCallDiscardReasonMissed) text = s.Outgoing ? "No answer" : "Missed call";
            else if (d.reason is PhoneCallDiscardReasonDisconnect) text = "Disconnected";
            else text = s.EstablishedUtc != null ? "Call ended" : (s.Outgoing ? "Declined" : "Call ended");
            bool needDebug = (d.flags & PhoneCallDiscarded.Flags.need_debug) != 0;
            End(s, text, needDebug);
        }

        // ── the instance ────────────────────────────────────────────────────────────────────────────────────────────

        private void StartNative(CallSession s, PhoneCall c)
        {
            string version = c.protocol != null && c.protocol.library_versions != null ? c.protocol.library_versions.FirstOrDefault() : null;
            if (string.IsNullOrEmpty(version)) throw new Exception("the server agreed no call version");
            var endpoints = new List<TgCalls.Endpoint>();
            var servers = new List<TgCalls.RtcServer>();
            var relays = c.connections ?? new PhoneConnectionBase[0];
            var reflectorIds = relays.OfType<PhoneConnection>().Select(x => x.id).Distinct().OrderBy(x => x).ToList();
            foreach (var conn in relays)
            {
                var r = conn as PhoneConnection;
                if (r != null)
                {
                    bool tcp = (r.flags & PhoneConnection.Flags.tcp) != 0;
                    if (!tcp && r.peer_tag != null && r.peer_tag.Length == 16)
                        endpoints.Add(new TgCalls.Endpoint
                        {
                            IdLo = (uint)(r.id & 0xFFFFFFFF), IdHi = (uint)((ulong)r.id >> 32),
                            Ipv4 = TgCalls.Fixed(r.ip, 48), Ipv6 = TgCalls.Fixed(r.ipv6, 64), Port = r.port, Tcp = 0, PeerTag = Tag(r.peer_tag),
                        });
                    string hex = r.peer_tag != null ? CallCrypto.Hex(r.peer_tag).ToLowerInvariant() : "";
                    int rid = reflectorIds.IndexOf(r.id) + 1;
                    foreach (string host in new[] { r.ip, r.ipv6 })
                        if (!string.IsNullOrEmpty(host))
                            servers.Add(Server(rid, host, r.port, "reflector", hex, true, tcp));
                    continue;
                }
                var w = conn as PhoneConnectionWebrtc;
                if (w == null) continue;
                foreach (string host in new[] { w.ip, w.ipv6 })
                {
                    if (string.IsNullOrEmpty(host)) continue;
                    if ((w.flags & PhoneConnectionWebrtc.Flags.stun) != 0) servers.Add(Server(0, host, w.port, null, null, false, false));
                    if ((w.flags & PhoneConnectionWebrtc.Flags.turn) != 0 && !string.IsNullOrEmpty(w.username) && !string.IsNullOrEmpty(w.password))
                        servers.Add(Server(0, host, w.port, w.username, w.password, true, false));
                }
            }
            bool p2p = (c.flags & PhoneCall.Flags.p2p_allowed) != 0;
            string custom = c.custom_parameters != null ? c.custom_parameters.data : null;
            Logger.Diag("[CALL] start " + version + " relays=" + endpoints.Count + " servers=" + servers.Count + " p2p=" + p2p);
            var camera = s.CameraOn && s.Camera != null ? s.Camera.Handle : IntPtr.Zero;
            var native = NativeCall.Create(version, s.Key, s.Outgoing, endpoints, servers, p2p, c.protocol.max_layer, custom,
                                           camera: camera, wire: n =>
            {
                n.StateChanged += st => Post(() => OnNativeState(s, st));
                n.SignalingEmitted += d => Post(() => { var t = SendSignalingAsync(s, d); });
                n.SignalBarsChanged += b => Post(() => { if (!s.Ended) { s.SignalBars = b; Raise(s); } });
                n.RemoteMediaChanged += (a, v) => Post(() => OnRemoteMedia(s, a, v));
            });
            if (native == null) throw new Exception("version " + version + " is not supported here");
            native.SetRemoteVideo(s.RemoteFrames, 960, 960);
            s.Native = native;
            if (s.Muted) native.SetMute(true);
            foreach (var d in s.EarlySignaling) native.ReceiveSignaling(d);
            s.EarlySignaling.Clear();
            s.Phase = CallPhase.Connecting;
            Raise(s);
        }

        private static byte[] Tag(byte[] t) { var b = new byte[16]; Array.Copy(t, b, Math.Min(16, t.Length)); return b; }

        private static TgCalls.RtcServer Server(int id, string host, int port, string login, string password, bool turn, bool tcp)
        {
            return new TgCalls.RtcServer
            {
                Id = id, Host = TgCalls.Fixed(host, 128), Port = port, Login = TgCalls.Fixed(login, 128),
                Password = TgCalls.Fixed(password, 128), IsTurn = turn ? 1 : 0, IsTcp = tcp ? 1 : 0,
            };
        }

        private void OnRemoteMedia(CallSession s, int audio, int video)
        {
            if (s.Ended) return;
            s.RemoteMuted = audio == 0;
            if (video != s.RemoteVideo)
            {
                s.RemoteVideo = video;
                if (video == 2) s.Video = true;
                s.RemoteFrames.Clear();   // off or paused: the avatar again (Telegram Desktop treats paused as off); on: no stale picture
                Logger.Diag("[CALL] remote video " + video);
            }
            Raise(s);
        }

        private void OnNativeState(CallSession s, int state)
        {
            if (s.Ended) return;
            Logger.Diag("[CALL] state " + state);
            if (state == TgCalls.StateEstablished)
            {
                s.Phase = CallPhase.Established;
                if (s.EstablishedUtc == null) s.EstablishedUtc = DateTime.UtcNow;
                if (s.CameraPending && s.Camera != null && s.Native != null) s.Native.SetCamera(s.Camera.Handle);
                s.CameraPending = false;
                s.CameraNote = null;   // "once the call is connected" and the like: it is now
                Raise(s);
            }
            else if (state == TgCalls.StateReconnecting) { s.Phase = CallPhase.Reconnecting; Raise(s); }
            else if (state == TgCalls.StateFailed) { var t = DiscardAsync(s, new PhoneCallDiscardReasonDisconnect(), "Connection failed"); }
        }

        private async Task SendSignalingAsync(CallSession s, byte[] data)
        {
            await s.SendGate.WaitAsync();   // in order: the peer's instance expects the packets as they were emitted
            try
            {
                if (s.Ended) return;
                bool ok = await Client.Phone_SendSignalingData(s.Input, data);
                if (!ok) Logger.Diag("[CALL] sendSignalingData returned false");
            }
            catch (Exception ex) { Logger.Diag("[CALL] sendSignalingData: " + ex.Message); }
            finally { s.SendGate.Release(); }
        }

        /// <summary>The call is over: the instance stops off the UI thread (it can take a moment); the debug log goes to the
        /// server when it asked for it.</summary>
        private void End(CallSession s, string text, bool needDebug = false)
        {
            if (s.Ended) return;
            s.Phase = CallPhase.Ended;
            s.EndText = text;
            var native = s.Native;
            s.Native = null;
            var camera = s.Camera;
            s.Camera = null;
            s.CameraPending = false;
            Logger.Diag("[CALL] ended: " + text + " after " + s.DurationSeconds + " s");
            Raise(s);
            // the camera closes at once, beside the call's own stop (the instance keeps its capture until it ends)
            var closing = camera != null ? Task.Run(() => { try { camera.Dispose(); } catch (Exception ex) { Logger.Diag("[CALL] camera close: " + ex.Message); } })
                                         : Task.FromResult(0);
            if (native == null)
            {
                _teardown = closing.ContinueWith(_ => { s.LocalVideo.Release(); s.RemoteFrames.Release(); });
                return;
            }
            var input = s.Input;
            _teardown = Task.Run(async () =>
            {
                string log = "";
                try
                {   // the device's side of the audio, for a trip log: gaps = the speaker ran dry, overruns = mic data lost
                    var a = native.Stats();
                    Logger.Diag("[CALL] audio: " + a.FramesRecorded + " mic frames, " + a.FramesRendered + " played, " +
                                a.PlaybackUnderruns + " speaker gaps, " + a.CaptureOverruns + " mic overruns");
                }
                catch { }
                try { log = native.Stop(5000); } catch (Exception ex) { Logger.Diag("[CALL] stop: " + ex.Message); }
                try { native.Dispose(); } catch { }
                try { closing.Wait(6000); } catch { }
                s.LocalVideo.Release();    // nothing calls back any more: unpin the picture buffers
                s.RemoteFrames.Release();
                if (needDebug && !string.IsNullOrEmpty(log))
                    try { await Client.Phone_SaveCallDebug(input, new DataJSON { data = log }); } catch (Exception ex) { Logger.Diag("[CALL] saveCallDebug: " + ex.Message); }
            });
        }

        /// <summary>The app is closing: hang up a live call (the peer is told; the request is on its way when this returns)
        /// and wait, bounded, until the call library and the camera have stopped (a live instance at exit is what hung
        /// the process before tgn_call_destroy drained tgcalls). On the UI thread, before the service goes.</summary>
        public void Shutdown(int timeoutMs)
        {
            var s = Current;
            if (s != null && !s.Ended) { var t = HangUpAsync(); }
            var td = _teardown;
            if (td != null) try { td.Wait(timeoutMs); } catch { }
        }

        private static string Friendly(Exception ex, string what)
        {
            var rpc = ex as WTelegram.WTException;
            string m = rpc != null ? rpc.Message : ex.Message;
            if (m.Contains("USER_PRIVACY_RESTRICTED")) return "This person doesn't accept calls from you.";
            if (m.Contains("PARTICIPANT_VERSION_OUTDATED")) return "This person's app can't take calls.";
            if (m.Contains("USER_IS_BLOCKED")) return "You have blocked this person, or they you.";
            return what + ": " + m;
        }
    }
}
