/* tgcalls_native: TelegArm's C ABI over Telegram's call library (tgcalls 1c236c09 + tg_owt d1cf250e, WebRTC M123, the
 * pair Telegram Desktop 7.3.1 ships), built as static libraries by the Venetium build server (TGCALLS-HANDOFF, 2026-10-16)
 * and linked into this DLL with rt2.1 for ARM32 and x86. C# P/Invokes it (Core/Calls/TgCalls.cs).
 *
 * ABI rules (ARM32 + x86, both ILP32): __cdecl; shared structs hold only int32/uint8/char fields (no int64: the ARM32
 * alignment-fault class); strings are UTF-8, zero-terminated. Callbacks arrive on WebRTC's threads: copy what you need
 * and return quickly; never call tgn_call_stop / tgn_call_destroy from inside a callback.
 *
 * Audio: tg_owt has no audio device code (built like tdesktop's), so the call's audio device is tgcalls'
 * FakeAudioDeviceModule with our recorder/renderer: WinMM waveIn/waveOut (the API TelegArm's voice notes already use on
 * RT), paced by the sound card's own clock. TGN_AUDIO_TEST replaces the devices with a test tone and an analyser (the
 * loopback self-test).
 *
 * Video (1.1): this DLL defines tgcalls::CreatePlatformInterface itself (the link replaces tdesktop's DirectShow platform),
 * so a camera is whatever the app pushes into a tgn_camera: TelegArm's Windows 10 frame reader, or on Windows 8.1 the
 * custom media sink below (MediaCapture.StartPreviewToCustomSinkAsync). Pictures come back as BGRA (B,G,R,A bytes, the
 * GDI 32 bpp order) through tgn_frame_fn: the local preview (mirrored) and the remote video (rotated upright). */
#ifndef TGCALLS_NATIVE_H
#define TGCALLS_NATIVE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define TGN_API __declspec(dllexport)
#define TGN_CALL __cdecl

typedef void (TGN_CALL *tgn_log_fn)(void *user, const char *line);

/* Once per process: WSAStartup (WebRTC never does), registers the call implementations as tdesktop does, routes tgcalls'
 * log to log (may be NULL). Returns 0. Safe to call again. */
TGN_API int TGN_CALL tgn_init(tgn_log_fn log, void *user);
TGN_API int TGN_CALL tgn_version(void);                    /* (major << 16) | minor */
TGN_API int TGN_CALL tgn_max_layer(void);                  /* tgcalls::Meta::MaxLayer() */
/* The supported library versions, ';'-separated, NEWEST FIRST (numeric order). Returns the length written. */
TGN_API int TGN_CALL tgn_versions(char *buf, int len);
/* The list for phoneCallProtocol.library_versions, as tdesktop sends it (CollectVersionsForApi: tgcalls' own order,
 * reversed), ';'-separated. Returns the length written. */
TGN_API int TGN_CALL tgn_versions_api(char *buf, int len);

typedef struct tgn_endpoint {        /* a phoneConnection (Telegram's relay) */
    uint32_t id_lo, id_hi;           /* the 64-bit endpoint id */
    char ipv4[48];
    char ipv6[64];
    int32_t port;
    int32_t tcp;                     /* 1 = TcpRelay, 0 = UdpRelay */
    uint8_t peer_tag[16];
} tgn_endpoint;

typedef struct tgn_rtc_server {      /* a phoneConnectionWebrtc (STUN or TURN) */
    int32_t id;
    char host[128];
    int32_t port;
    char login[128];
    char password[128];
    int32_t is_turn;
    int32_t is_tcp;
} tgn_rtc_server;

#define TGN_AUDIO_DEVICES 0          /* WinMM: the default microphone and speaker */
#define TGN_AUDIO_TEST    1          /* a test tone of test_tone_hz; what is received is analysed (tgn_call_audio_stats) */
#define TGN_AUDIO_SPEAKER_TEST 2     /* the test tone out, and what is received ALSO played through the speaker at 1/10 volume
                                        (the speaker path's own self-test: tgn_call_audio_stats counts its gaps) */

/* ── video: the camera (1.1) ──────────────────────────────────────────────────────────────────────────────────────── */
#define TGN_PIX_NV12 1               /* plane0 Y, plane1 interleaved UV */
#define TGN_PIX_YUY2 2               /* plane0 packed Y0 U Y1 V */
#define TGN_PIX_BGRA 3               /* plane0 B,G,R,A bytes (a negative stride = bottom-up) */
#define TGN_PIX_I420 4               /* plane0 Y, plane1 U, plane2 V */

typedef struct tgn_frame_in {        /* one camera picture; the pointers need only live until tgn_camera_push returns */
    int32_t format;                  /* TGN_PIX_* */
    int32_t width, height;
    const uint8_t *plane0; int32_t stride0;
    const uint8_t *plane1; int32_t stride1;
    const uint8_t *plane2; int32_t stride2;
    int32_t rotation;                /* degrees clockwise that make the picture upright: 0, 90, 180, 270 */
} tgn_frame_in;

/* A picture handed out: BGRA, top-down, valid only during the call. Arrives on a camera or WebRTC thread. */
typedef void (TGN_CALL *tgn_frame_fn)(void *user, const uint8_t *bgra, int32_t width, int32_t height, int32_t stride);

typedef struct tgn_camera tgn_camera;

/* A camera for calls: pictures larger than max_w x max_h are scaled down before they are sent. */
TGN_API tgn_camera *TGN_CALL tgn_camera_create(int32_t max_w, int32_t max_h);
/* Converts the picture now (I420, rotated upright) and hands it to every call using this camera. Push from one thread at
 * a time. Returns 0 sent, 1 dropped (no call wants a picture now, or WebRTC's adapter shed it), negative on bad input. */
TGN_API int  TGN_CALL tgn_camera_push(tgn_camera *cam, const tgn_frame_in *f);
/* The local picture for the app: fitted in max_w x max_h, at most fps a second, mirrored when mirror (a front camera, like
 * a mirror; a rear one is not); cb NULL stops. */
TGN_API void TGN_CALL tgn_camera_set_preview(tgn_camera *cam, tgn_frame_fn cb, void *user, int32_t max_w, int32_t max_h, int32_t fps,
                                             int32_t mirror);
/* Stops the preview; calls still using the camera keep it until they are given another one or end. */
TGN_API void TGN_CALL tgn_camera_destroy(tgn_camera *cam);

typedef struct tgn_camera_stats {
    int32_t pushed, sent, dropped;   /* pictures offered, sent to WebRTC, dropped */
    int32_t previews;                /* preview pictures handed out */
    int32_t width, height;           /* the last picture sent */
} tgn_camera_stats;
TGN_API int TGN_CALL tgn_camera_stats_get(tgn_camera *cam, tgn_camera_stats *out);

/* Windows 8.1 (no MediaFrameReader): a Media Foundation media sink, as an IInspectable* that implements
 * Windows.Media.IMediaExtension, for MediaCapture.StartPreviewToCustomSinkAsync(profile, sink). Every sample it is given
 * (NV12, YUY2 or RGB32) is pushed into cam. The caller owns one reference (Marshal.Release after wrapping it). NULL when
 * Media Foundation is missing. width/height/fps: the media type it offers first. */
TGN_API void *TGN_CALL tgn_camsink_create(tgn_camera *cam, int32_t width, int32_t height, int32_t fps);

#define TGN_OPT_H264_ENCODE 1        /* 0: never ENCODE H.264 (we still decode it); VP8 then. Default 1. Before the first call. */
TGN_API void TGN_CALL tgn_set_option(int32_t option, int32_t value);
/* Video encoders created in this process so far, e.g. "VP8 1, H264 0, VP9 0". Returns the length written. */
TGN_API int  TGN_CALL tgn_encoders(char *buf, int32_t len);

typedef struct tgn_call_params {
    const char *version;             /* the library version agreed for this call */
    const uint8_t *key;              /* the 256-byte auth key from the DH exchange */
    int32_t is_outgoing;
    const tgn_endpoint *endpoints;
    int32_t endpoint_count;
    const tgn_rtc_server *servers;
    int32_t server_count;
    int32_t enable_p2p;
    int32_t allow_tcp;
    int32_t max_layer;               /* the protocol's max_layer */
    const char *custom_parameters;   /* phone.getCallConfig's JSON, or NULL */
    int32_t audio_mode;              /* TGN_AUDIO_* */
    int32_t test_tone_hz;
    tgn_camera *camera;              /* 1.1: send this camera from the start (a video call), or NULL */
} tgn_call_params;

typedef struct tgn_callbacks {
    void *user;
    void (TGN_CALL *state)(void *user, int32_t state);                /* tgcalls::State: 0 WaitInit 1 WaitInitAck 2 Established 3 Failed 4 Reconnecting */
    void (TGN_CALL *signaling)(void *user, const uint8_t *data, int32_t len);   /* send with phone.sendSignalingData */
    void (TGN_CALL *signal_bars)(void *user, int32_t bars);
    void (TGN_CALL *audio_level)(void *user, float level);
    void (TGN_CALL *remote_media)(void *user, int32_t audio_state, int32_t video_state);   /* AudioState 0 muted 1 active; VideoState 0 inactive 1 paused 2 active */
    void (TGN_CALL *remote_battery_low)(void *user, int32_t low);
} tgn_callbacks;

typedef struct tgn_call tgn_call;

TGN_API tgn_call *TGN_CALL tgn_call_create(const tgn_call_params *p, const tgn_callbacks *cb);
TGN_API void TGN_CALL tgn_call_receive_signaling(tgn_call *c, const uint8_t *data, int32_t len);   /* updatePhoneCallSignalingData */
TGN_API void TGN_CALL tgn_call_set_mute(tgn_call *c, int32_t mute);
/* Stops the call and waits up to timeout_ms for tgcalls to finish; the debug log (for phone.saveCallDebug) goes into
 * debug_log. Returns 0 when it stopped, 1 on the timeout. Never from a callback. */
TGN_API int  TGN_CALL tgn_call_stop(tgn_call *c, char *debug_log, int32_t len, int32_t timeout_ms);
TGN_API void TGN_CALL tgn_call_destroy(tgn_call *c);

typedef struct tgn_audio_stats {
    int32_t frames_recorded;         /* 10 ms frames handed to WebRTC */
    int32_t frames_rendered;         /* 10 ms frames WebRTC played (non-silent ones counted below) */
    int32_t frames_audible;
    int32_t rendered_hz;             /* TGN_AUDIO_TEST: the dominant frequency received (zero crossings) */
    int32_t rendered_rms;
    int32_t capture_overruns;
    int32_t playback_underruns;
} tgn_audio_stats;

TGN_API int TGN_CALL tgn_call_audio_stats(tgn_call *c, tgn_audio_stats *out);

/* 1.1 video. Starts (cam) or stops (NULL) sending a camera. Under the V1 libraries (2.7.7, 5.0.0) only once connected. */
TGN_API void TGN_CALL tgn_call_set_camera(tgn_call *c, tgn_camera *cam);
/* The remote video: each picture rotated upright and fitted in max_w x max_h; cb NULL stops. Any time, any number of
 * times (the sink is attached to the call once, at create). */
TGN_API void TGN_CALL tgn_call_set_remote_video(tgn_call *c, tgn_frame_fn cb, void *user, int32_t max_w, int32_t max_h);

typedef struct tgn_video_stats {
    int32_t remote_frames;           /* remote pictures decoded */
    int32_t remote_width, remote_height, remote_rotation;   /* the last one, as decoded (before rotating) */
} tgn_video_stats;
TGN_API int TGN_CALL tgn_call_video_stats(tgn_call *c, tgn_video_stats *out);

/* Two calls in this process, wired to each other (signaling handed over directly, ICE over this machine's own
 * addresses, no Telegram server), each sending a test tone: 0 when both connect and each hears the other's tone within
 * `seconds`. A report goes into `report`. */
TGN_API int TGN_CALL tgn_selftest_loopback(int32_t seconds, char *report, int32_t len);
/* The same, with B in b_mode (TGN_AUDIO_SPEAKER_TEST: B plays A's tone through this PC's speaker, quietly, for the whole
 * `seconds`, and the report counts the speaker's gaps). */
TGN_API int TGN_CALL tgn_selftest_audio(int32_t b_mode, int32_t seconds, char *report, int32_t len);

#define TGN_SELFTEST_NO_H264 1       /* encode VP8 (TGN_OPT_H264_ENCODE 0) for the duration */
/* The same pair, A sending a test picture (red left half, blue right half, a moving white square) from a camera: B must
 * see it the right way round, A's preview mirrored; then A's camera goes off (B: remote video inactive) and on again.
 * version NULL = the newest. 0 on success; a report goes into `report`. */
TGN_API int TGN_CALL tgn_selftest_video(const char *version, int32_t seconds, int32_t flags, char *report, int32_t len);

/* ── group calls: voice / video chats (1.2) ──────────────────────────────────────────────────────────────────────────
 * tgcalls' GroupInstanceCustomImpl with the same audio (WinMM, AudioScheduler) and cameras. The flow (as Telegram
 * Desktop): create, emit the join payload, send its JSON in phone.joinGroupCall, hand the server's
 * updateGroupCallConnection JSON to set_join_response, set the RTC mode. The instance starts MUTED with the microphone
 * closed (it opens on the first unmute). Rejoin: mode NONE (new ssrc, ufrag, keys), emit again, join again. */
typedef struct tgn_group tgn_group;

#define TGN_GROUP_MODE_NONE 0
#define TGN_GROUP_MODE_RTC  1

typedef struct tgn_group_params {
    int32_t audio_mode;              /* TGN_AUDIO_* */
    int32_t test_tone_hz;
    int32_t video;                   /* 1: video-capable (the join payload carries the camera's ssrc-groups; pictures in) */
    int32_t noise_suppression;
    int32_t prefer_vp8;              /* send VP8 (ARM32: no SIMD in OpenH264); H.264 is still received */
    tgn_camera *camera;              /* sent from the start, or NULL */
} tgn_group_params;

typedef struct tgn_group_level {     /* every 100 ms per sounding ssrc; ssrc 0 = us */
    uint32_t ssrc;
    int32_t level_milli;             /* tgcalls' level x 1000 (0..~2000) */
    int32_t voice;                   /* the speech bit */
    int32_t muted;
} tgn_group_level;

typedef struct tgn_group_callbacks {
    void *user;
    void (TGN_CALL *network)(void *user, int32_t connected, int32_t mode);
    void (TGN_CALL *join_payload)(void *user, uint32_t audio_ssrc, const char *json, int32_t len);   /* after emit_join */
    void (TGN_CALL *levels)(void *user, const tgn_group_level *items, int32_t count);
    void (TGN_CALL *signal_bars)(void *user, int32_t bars);
} tgn_group_callbacks;

typedef struct tgn_ssrc_group {      /* a GroupCallParticipantVideoSourceGroup */
    char semantics[8];               /* "SIM", "FID" */
    int32_t count;
    uint32_t ssrc[8];
} tgn_ssrc_group;

#define TGN_QUALITY_THUMBNAIL 0      /* 180 p */
#define TGN_QUALITY_MEDIUM    1      /* 360 p */
#define TGN_QUALITY_FULL      2      /* 720 p */

typedef struct tgn_group_video {     /* a picture we want: a participant's GroupCallParticipantVideo */
    char endpoint[128];
    uint32_t audio_ssrc;
    uint32_t user_lo, user_hi;
    int32_t min_quality, max_quality;
    int32_t group_count;
    tgn_ssrc_group groups[4];
} tgn_group_video;

TGN_API tgn_group *TGN_CALL tgn_group_create(const tgn_group_params *p, const tgn_group_callbacks *cb);
TGN_API void TGN_CALL tgn_group_emit_join(tgn_group *g);
/* The server's JSON (updateGroupCallConnection.params). 0 accepted, 1 not a join response (checked first: tgcalls itself
 * would only log it). */
TGN_API int  TGN_CALL tgn_group_set_join_response(tgn_group *g, const char *json);
TGN_API void TGN_CALL tgn_group_set_mode(tgn_group *g, int32_t mode);
TGN_API void TGN_CALL tgn_group_set_mute(tgn_group *g, int32_t mute);
TGN_API void TGN_CALL tgn_group_set_camera(tgn_group *g, tgn_camera *cam);     /* NULL stops */
/* The pictures wanted, replacing the last list (tgcalls drops the channels not listed). */
TGN_API void TGN_CALL tgn_group_set_video_channels(tgn_group *g, const tgn_group_video *v, int32_t count);
/* A participant's pictures: rotated upright, fitted in max_w x max_h, at most max_fps a second (0 = all); cb NULL stops. */
TGN_API void TGN_CALL tgn_group_set_video_output(tgn_group *g, const char *endpoint, tgn_frame_fn cb, void *user, int32_t max_w,
                                                 int32_t max_h, int32_t max_fps);
TGN_API void TGN_CALL tgn_group_set_volume(tgn_group *g, uint32_t ssrc, int32_t permille);   /* 1000 = 100 % */
TGN_API int  TGN_CALL tgn_group_audio_stats(tgn_group *g, tgn_audio_stats *out);
/* Stops and waits (up to timeout_ms) for tgcalls; 0 stopped, 1 timed out. Never from a callback. */
TGN_API int  TGN_CALL tgn_group_stop(tgn_group *g, int32_t timeout_ms);
TGN_API void TGN_CALL tgn_group_destroy(tgn_group *g);

/* Offline (no server): the join payload (voice and video), a rejoin's new ssrc, a synthetic join response accepted and a
 * malformed one refused, the 100 ms levels with ssrc 0, mute, clean teardown. 0 on success; a report. */
TGN_API int TGN_CALL tgn_selftest_group(int32_t flags, char *report, int32_t len);

#ifdef __cplusplus
}
#endif
#endif
