/* tgav: TelegArm's media shim over FFmpeg (ffmpeg-rt 8.1.2). A flat C API for the C# player, so the managed side never
 * reads an FFmpeg struct (their layouts change between FFmpeg versions; this header does not).
 *
 * ABI rules (ARM32 + x86, both ILP32):
 *  - __cdecl everywhere (the only convention on ARM; explicit on x86, matching [DllImport(CallingConvention.Cdecl)]).
 *  - Shared structs hold only int32 fields: no int64 (the ARM32 alignment-fault class the camera handoff warns about).
 *  - Times are milliseconds in int32 (24 days of media).
 *  - Errors are negative FFmpeg AVERROR codes; tgav_strerror describes them.
 *
 * Threads: one thread may call tgav_read_video while another calls tgav_read_audio on the same handle (the demuxer and
 * the packet queues are under a lock; each decoder belongs to its reader). tgav_seek / tgav_set_tempo / tgav_close must
 * not run while a read is in progress: the C# player pauses its readers first. */
#ifndef TGAV_H
#define TGAV_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define TGAV_API __declspec(dllexport)
#define TGAV_CALL __cdecl

/* tgav_open flags */
#define TGAV_NO_AUDIO       1   /* ignore the audio stream (stickers, GIFs, frame grabs) */
#define TGAV_NO_VIDEO       2   /* ignore the video stream (voice notes, music) */
#define TGAV_SINGLE_THREAD  4   /* one decoder thread (small clips: less memory, less latency) */

typedef struct tgav tgav;

typedef struct tgav_info {
    int32_t has_video;
    int32_t width;          /* DISPLAY size: after the stream's rotation (a portrait phone video reports portrait) */
    int32_t height;
    int32_t rotation;       /* 0, 90, 180, 270: already applied to frames from tgav_read_video */
    int32_t fps_num;        /* average frame rate (0/0 if unknown) */
    int32_t fps_den;
    int32_t has_audio;
    int32_t sample_rate;    /* the stream's own */
    int32_t channels;
    int32_t duration_ms;    /* 0 if unknown */
    int32_t video_codec;    /* AVCodecID, for diagnostics */
    int32_t audio_codec;
} tgav_info;

TGAV_API int  TGAV_CALL tgav_version(void);                       /* (major << 16) | minor */
TGAV_API int  TGAV_CALL tgav_ffmpeg_version(void);                /* avcodec_version() */
TGAV_API void TGAV_CALL tgav_strerror(int err, char *buf, int len);

/* path is UTF-16 (C# string). NULL on failure; *err gets the AVERROR. */
TGAV_API tgav *TGAV_CALL tgav_open(const wchar_t *path, int flags, int *err);
TGAV_API int  TGAV_CALL tgav_get_info(tgav *t, tgav_info *out);

/* The next video frame, scaled to w x h BGRA (32 bpp, the bytes of a Format32bppRgb/Argb GDI+ bitmap) into dst.
 * Returns 1 = a frame, 0 = end of stream, < 0 = error. *pts_ms = its presentation time (stream start = 0).
 * dst NULL: the frame is decoded (the decoder must see every frame) but not converted: how a late frame is dropped. */
TGAV_API int  TGAV_CALL tgav_read_video(tgav *t, uint8_t *dst, int dst_stride, int w, int h, int *pts_ms);

/* Audio comes out as interleaved signed 16-bit at the rate/channels set here (call before the first read; default
 * 48000 Hz stereo), sped up by tgav_set_tempo (0.5 .. 4.0, pitch kept: FFmpeg's atempo). */
TGAV_API int  TGAV_CALL tgav_set_audio_format(tgav *t, int sample_rate, int channels);
TGAV_API int  TGAV_CALL tgav_set_tempo(tgav *t, int tempo_percent);
/* Up to max_samples samples PER CHANNEL into dst. Returns the count written (> 0), 0 = end, < 0 = error.
 * *pts_ms = the MEDIA time of the first sample (tempo already accounted for). */
TGAV_API int  TGAV_CALL tgav_read_audio(tgav *t, int16_t *dst, int max_samples, int *pts_ms);

/* Seek to ms (the frame/sample at or before it is the next one out; earlier ones are decoded and skipped). */
TGAV_API int  TGAV_CALL tgav_seek(tgav *t, int ms);
TGAV_API void TGAV_CALL tgav_close(tgav *t);

#ifdef __cplusplus
}
#endif
#endif
