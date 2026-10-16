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
 * loopback self-test). */
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

/* Two calls in this process, wired to each other (signaling handed over directly, ICE over this machine's own
 * addresses, no Telegram server), each sending a test tone: 0 when both connect and each hears the other's tone within
 * `seconds`. A report goes into `report`. */
TGN_API int TGN_CALL tgn_selftest_loopback(int32_t seconds, char *report, int32_t len);

#ifdef __cplusplus
}
#endif
#endif
