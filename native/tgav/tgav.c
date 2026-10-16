/* tgav: TelegArm's media shim over FFmpeg 8.1.2 (ffmpeg-rt). See tgav.h for the API and its rules.
 * Built with rt2.1 clang-cl for armv7-pc-windows-msvc (the Surface) and i686-pc-windows-msvc (every Intel/AMD PC: the
 * exe is AnyCPU + Prefer32Bit), static CRT (-MT): build.ps1. Replaces libVLC for playback (2026-10-16).
 *
 * Shape: one demuxer, one packet queue per used stream (so the audio thread and the video thread each pull their own
 * packets: whichever reader needs a packet demuxes, under the lock, and queues what belongs to the other). Video goes
 * through swscale to BGRA at the caller's size, with the stream's display rotation applied. Audio goes through a small
 * filter graph: abuffer -> [atempo] -> aresample -> aformat(s16, rate, channels) -> abuffersink. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <math.h>
#include <stdio.h>
#include <string.h>

#include "tgav.h"

#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/channel_layout.h>
#include <libavutil/display.h>
#include <libavutil/opt.h>
#include <libswscale/swscale.h>
#include <libavfilter/avfilter.h>
#include <libavfilter/buffersink.h>
#include <libavfilter/buffersrc.h>

#define TGAV_MAJOR 1
#define TGAV_MINOR 1   /* 1.1: tgav_scale (camera frames) */

/* ── packet queue ───────────────────────────────────────────────────────────────────────────────────────────────── */
typedef struct pnode { AVPacket *pkt; struct pnode *next; } pnode;
typedef struct pktq { pnode *head, *tail; int count; } pktq;

static int q_push(pktq *q, AVPacket *p)
{
    pnode *n = (pnode *)av_mallocz(sizeof(pnode));
    if (!n) return AVERROR(ENOMEM);
    n->pkt = p;
    if (q->tail) q->tail->next = n; else q->head = n;
    q->tail = n;
    q->count++;
    return 0;
}

static AVPacket *q_pop(pktq *q)
{
    pnode *n = q->head;
    AVPacket *p;
    if (!n) return NULL;
    q->head = n->next;
    if (!q->head) q->tail = NULL;
    q->count--;
    p = n->pkt;
    av_free(n);
    return p;
}

static void q_clear(pktq *q)
{
    AVPacket *p;
    while ((p = q_pop(q)) != NULL) av_packet_free(&p);
}

/* ── the handle ─────────────────────────────────────────────────────────────────────────────────────────────────── */
struct tgav {
    AVFormatContext *fmt;
    CRITICAL_SECTION lock;          /* the demuxer + both queues */
    int64_t start_us;               /* the container's start time: stream time 0 for both streams */
    int demux_eof;

    int vidx;                       /* -1: none (absent, disabled, or no decoder) */
    AVCodecContext *vdec;
    pktq vq;
    AVFrame *vframe;
    struct SwsContext *sws;
    uint8_t *rot_buf;
    int rot_buf_size;
    int rotation;                   /* clockwise degrees to apply: 0/90/180/270 */
    int v_flushed;                  /* the NULL (drain) packet was sent */
    int v_skip_ms;                  /* after a seek: drop frames that end before this (-1 = none) */

    int aidx;
    AVCodecContext *adec;
    pktq aq;
    AVFrame *aframe;                /* decoded */
    AVFrame *afilt;                 /* filtered, being handed out */
    int afilt_off;                  /* samples of afilt already handed out */
    AVFilterGraph *graph;
    AVFilterContext *src, *sink;
    int graph_dirty;
    int a_flushed;                  /* the decoder got its NULL packet */
    int a_graph_flushed;            /* the graph got its NULL frame */
    int a_skip_ms;
    int out_rate, out_ch, tempo_pct;
    int a_base_in_ms, a_base_out_ms;/* media time = base_in + (out - base_out) * tempo */
    int a_have_base;
};

static int ms_of(tgav *t, AVStream *st, int64_t pts)
{
    if (pts == AV_NOPTS_VALUE) return -1;
    return (int)(av_rescale_q(pts, st->time_base, (AVRational){1, 1000}) - t->start_us / 1000);
}

/* Under the lock: make sure q has a packet. 1 = it has, 0 = the file is exhausted. */
static int demux_for(tgav *t, pktq *q)
{
    while (q->count == 0) {
        AVPacket *p;
        int r;
        if (t->demux_eof) return 0;
        p = av_packet_alloc();
        if (!p) return AVERROR(ENOMEM);
        r = av_read_frame(t->fmt, p);
        if (r < 0) {                         /* EOF, or a read error that ends the stream the same way */
            av_packet_free(&p);
            t->demux_eof = 1;
            return 0;
        }
        if (p->stream_index == t->vidx) r = q_push(&t->vq, p);
        else if (p->stream_index == t->aidx) r = q_push(&t->aq, p);
        else { av_packet_free(&p); r = 0; }
        if (r < 0) { av_packet_free(&p); return r; }
    }
    return 1;
}

/* The next decoded frame of one stream into f: 1 = a frame, 0 = end, < 0 = error. */
static int decode_next(tgav *t, AVCodecContext *dec, pktq *q, int *flushed, AVFrame *f)
{
    for (;;) {
        AVPacket *p = NULL;
        int r = avcodec_receive_frame(dec, f);
        if (r == 0) return 1;
        if (r == AVERROR_EOF) return 0;
        if (r != AVERROR(EAGAIN)) return r;
        EnterCriticalSection(&t->lock);
        r = demux_for(t, q);
        if (r > 0) p = q_pop(q);
        LeaveCriticalSection(&t->lock);
        if (r < 0) return r;
        if (p) {
            r = avcodec_send_packet(dec, p);
            av_packet_free(&p);
            if (r < 0 && r != AVERROR(EAGAIN) && r != AVERROR_INVALIDDATA) return r;   /* a damaged packet is skipped */
        } else {
            if (*flushed) return 0;
            avcodec_send_packet(dec, NULL);   /* drain the frames the decoder still holds */
            *flushed = 1;
        }
    }
}

/* ── open / info / close ───────────────────────────────────────────────────────────────────────────────────────── */
static AVCodecContext *open_decoder(AVStream *st, int single_thread)
{
    const AVCodec *codec = avcodec_find_decoder(st->codecpar->codec_id);
    AVCodecContext *c;
    if (!codec) return NULL;
    c = avcodec_alloc_context3(codec);
    if (!c) return NULL;
    if (avcodec_parameters_to_context(c, st->codecpar) < 0) { avcodec_free_context(&c); return NULL; }
    c->pkt_timebase = st->time_base;
    c->thread_count = single_thread ? 1 : 0;   /* 0 = one per core (4 on the Tegra) */
    c->thread_type = FF_THREAD_FRAME | FF_THREAD_SLICE;
    if (avcodec_open2(c, codec, NULL) < 0) { avcodec_free_context(&c); return NULL; }
    return c;
}

static int stream_rotation(AVStream *st)
{
    const AVPacketSideData *sd = av_packet_side_data_get(st->codecpar->coded_side_data, st->codecpar->nb_coded_side_data,
                                                         AV_PKT_DATA_DISPLAYMATRIX);
    double th;
    int r;
    if (!sd || sd->size < 9 * 4) return 0;
    th = -av_display_rotation_get((const int32_t *)sd->data);   /* as ffplay's get_rotation: clockwise degrees */
    if (isnan(th)) return 0;
    r = (int)floor(th / 90.0 + 0.5) * 90;
    r %= 360;
    if (r < 0) r += 360;
    return r;
}

TGAV_API int TGAV_CALL tgav_version(void) { return (TGAV_MAJOR << 16) | TGAV_MINOR; }
TGAV_API int TGAV_CALL tgav_ffmpeg_version(void) { return (int)avcodec_version(); }

TGAV_API void TGAV_CALL tgav_strerror(int err, char *buf, int len)
{
    if (!buf || len <= 0) return;
    if (av_strerror(err, buf, (size_t)len) < 0) snprintf(buf, (size_t)len, "error %d", err);
}

TGAV_API tgav *TGAV_CALL tgav_open(const wchar_t *path, int flags, int *err)
{
    char u8[2048];
    AVFormatContext *fmt = NULL;
    tgav *t;
    int r, i;
    if (err) *err = 0;
    av_log_set_level(AV_LOG_QUIET);   /* a GUI process: FFmpeg's stderr goes nowhere */
    if (!path || !WideCharToMultiByte(CP_UTF8, 0, path, -1, u8, (int)sizeof(u8), NULL, NULL)) {
        if (err) *err = AVERROR(EINVAL);
        return NULL;
    }
    r = avformat_open_input(&fmt, u8, NULL, NULL);   /* FFmpeg's file protocol takes UTF-8 paths on Windows */
    if (r < 0) { if (err) *err = r; return NULL; }
    r = avformat_find_stream_info(fmt, NULL);
    if (r < 0) { avformat_close_input(&fmt); if (err) *err = r; return NULL; }

    t = (tgav *)av_mallocz(sizeof(tgav));
    if (!t) { avformat_close_input(&fmt); if (err) *err = AVERROR(ENOMEM); return NULL; }
    InitializeCriticalSection(&t->lock);
    t->fmt = fmt;
    t->start_us = fmt->start_time != AV_NOPTS_VALUE ? fmt->start_time : 0;
    t->vidx = t->aidx = -1;
    t->v_skip_ms = t->a_skip_ms = -1;
    t->out_rate = 48000; t->out_ch = 2; t->tempo_pct = 100;
    t->graph_dirty = 1;

    if (!(flags & TGAV_NO_VIDEO)) {
        i = av_find_best_stream(fmt, AVMEDIA_TYPE_VIDEO, -1, -1, NULL, 0);
        /* an mp3's cover art is a "video" stream of one picture: not a video */
        if (i >= 0 && !(fmt->streams[i]->disposition & AV_DISPOSITION_ATTACHED_PIC)) {
            t->vdec = open_decoder(fmt->streams[i], flags & TGAV_SINGLE_THREAD);
            if (t->vdec) { t->vidx = i; t->rotation = stream_rotation(fmt->streams[i]); }
        }
    }
    if (!(flags & TGAV_NO_AUDIO)) {
        i = av_find_best_stream(fmt, AVMEDIA_TYPE_AUDIO, -1, t->vidx, NULL, 0);
        if (i >= 0) {
            t->adec = open_decoder(fmt->streams[i], 1);
            if (t->adec) t->aidx = i;
        }
    }
    if (t->vidx < 0 && t->aidx < 0) {
        tgav_close(t);
        if (err) *err = AVERROR_STREAM_NOT_FOUND;   /* nothing playable (or no decoder for what is there) */
        return NULL;
    }
    for (i = 0; i < (int)fmt->nb_streams; i++)
        if (i != t->vidx && i != t->aidx) fmt->streams[i]->discard = AVDISCARD_ALL;
    t->vframe = av_frame_alloc();
    t->aframe = av_frame_alloc();
    t->afilt = av_frame_alloc();
    if (!t->vframe || !t->aframe || !t->afilt) { tgav_close(t); if (err) *err = AVERROR(ENOMEM); return NULL; }
    return t;
}

TGAV_API int TGAV_CALL tgav_get_info(tgav *t, tgav_info *o)
{
    if (!t || !o) return AVERROR(EINVAL);
    memset(o, 0, sizeof(*o));
    if (t->vidx >= 0) {
        AVStream *st = t->fmt->streams[t->vidx];
        int w = st->codecpar->width, h = st->codecpar->height;
        AVRational sar = st->sample_aspect_ratio.num ? st->sample_aspect_ratio : st->codecpar->sample_aspect_ratio;
        if (sar.num > 0 && sar.den > 0 && sar.num != sar.den) w = (int)av_rescale(w, sar.num, sar.den);   /* anamorphic */
        o->has_video = 1;
        o->rotation = t->rotation;
        o->width = (t->rotation == 90 || t->rotation == 270) ? h : w;
        o->height = (t->rotation == 90 || t->rotation == 270) ? w : h;
        o->fps_num = st->avg_frame_rate.num;
        o->fps_den = st->avg_frame_rate.den;
        o->video_codec = (int32_t)st->codecpar->codec_id;
    }
    if (t->aidx >= 0) {
        AVStream *st = t->fmt->streams[t->aidx];
        o->has_audio = 1;
        o->sample_rate = st->codecpar->sample_rate;
        o->channels = st->codecpar->ch_layout.nb_channels;
        o->audio_codec = (int32_t)st->codecpar->codec_id;
    }
    if (t->fmt->duration != AV_NOPTS_VALUE && t->fmt->duration > 0)
        o->duration_ms = (int32_t)(t->fmt->duration / 1000);
    return 0;
}

static void free_graph(tgav *t)
{
    if (t->graph) avfilter_graph_free(&t->graph);   /* frees src/sink too */
    t->src = t->sink = NULL;
}

TGAV_API void TGAV_CALL tgav_close(tgav *t)
{
    if (!t) return;
    q_clear(&t->vq);
    q_clear(&t->aq);
    free_graph(t);
    if (t->sws) sws_freeContext(t->sws);
    av_freep(&t->rot_buf);
    av_frame_free(&t->vframe);
    av_frame_free(&t->aframe);
    av_frame_free(&t->afilt);
    avcodec_free_context(&t->vdec);
    avcodec_free_context(&t->adec);
    if (t->fmt) avformat_close_input(&t->fmt);
    DeleteCriticalSection(&t->lock);
    av_free(t);
}

/* ── video ──────────────────────────────────────────────────────────────────────────────────────────────────────── */
static void rotate32(const uint32_t *src, int sw, int sh, uint8_t *dst, int stride, int rot)
{
    int x, y;
    if (rot == 180) {          /* dst is sw x sh */
        for (y = 0; y < sh; y++) {
            uint32_t *d = (uint32_t *)(dst + (size_t)y * stride);
            const uint32_t *s = src + (size_t)(sh - 1 - y) * sw;
            for (x = 0; x < sw; x++) d[x] = s[sw - 1 - x];
        }
    } else if (rot == 90) {    /* clockwise: dst is sh x sw; dst(x, y) = src(y, sh - 1 - x) */
        for (y = 0; y < sw; y++) {
            uint32_t *d = (uint32_t *)(dst + (size_t)y * stride);
            for (x = 0; x < sh; x++) d[x] = src[(size_t)(sh - 1 - x) * sw + y];
        }
    } else {                   /* 270, counter-clockwise: dst(x, y) = src(sw - 1 - y, x) */
        for (y = 0; y < sw; y++) {
            uint32_t *d = (uint32_t *)(dst + (size_t)y * stride);
            for (x = 0; x < sh; x++) d[x] = src[(size_t)x * sw + (sw - 1 - y)];
        }
    }
}

static int convert_frame(tgav *t, uint8_t *dst, int stride, int w, int h)
{
    AVFrame *f = t->vframe;
    int rot = t->rotation;
    int sw = (rot == 90 || rot == 270) ? h : w;   /* the scaled size BEFORE the rotation */
    int sh = (rot == 90 || rot == 270) ? w : h;
    uint8_t *d[4] = { NULL, NULL, NULL, NULL };
    int ls[4] = { 0, 0, 0, 0 };
    t->sws = sws_getCachedContext(t->sws, f->width, f->height, (enum AVPixelFormat)f->format,
                                  sw, sh, AV_PIX_FMT_BGRA, SWS_BILINEAR, NULL, NULL, NULL);
    if (!t->sws) return AVERROR(EINVAL);
    if (rot == 0) {
        d[0] = dst; ls[0] = stride;
        sws_scale(t->sws, (const uint8_t *const *)f->data, f->linesize, 0, f->height, d, ls);
        return 0;
    }
    if (t->rot_buf_size < sw * sh * 4) {
        av_freep(&t->rot_buf);
        t->rot_buf = (uint8_t *)av_malloc((size_t)sw * sh * 4);
        t->rot_buf_size = t->rot_buf ? sw * sh * 4 : 0;
        if (!t->rot_buf) return AVERROR(ENOMEM);
    }
    d[0] = t->rot_buf; ls[0] = sw * 4;
    sws_scale(t->sws, (const uint8_t *const *)f->data, f->linesize, 0, f->height, d, ls);
    rotate32((const uint32_t *)t->rot_buf, sw, sh, dst, stride, rot);
    return 0;
}

TGAV_API int TGAV_CALL tgav_read_video(tgav *t, uint8_t *dst, int dst_stride, int w, int h, int *pts_ms)
{
    AVStream *st;
    if (!t || t->vidx < 0) return AVERROR(EINVAL);
    if (dst && (w <= 0 || h <= 0 || dst_stride < w * 4)) return AVERROR(EINVAL);   /* dst NULL: decode, skip the conversion */
    st = t->fmt->streams[t->vidx];
    for (;;) {
        int ms, r = decode_next(t, t->vdec, &t->vq, &t->v_flushed, t->vframe);
        if (r <= 0) return r;
        ms = ms_of(t, st, t->vframe->best_effort_timestamp != AV_NOPTS_VALUE ? t->vframe->best_effort_timestamp : t->vframe->pts);
        if (t->v_skip_ms >= 0) {
            int dur = t->vframe->duration > 0 ? (int)av_rescale_q(t->vframe->duration, st->time_base, (AVRational){1, 1000}) : 1;
            if (ms >= 0 && ms + dur <= t->v_skip_ms) { av_frame_unref(t->vframe); continue; }   /* ends before the target */
            t->v_skip_ms = -1;
        }
        r = dst ? convert_frame(t, dst, dst_stride, w, h) : 0;   /* a late frame the player drops: no swscale cost */
        av_frame_unref(t->vframe);
        if (pts_ms) *pts_ms = ms < 0 ? 0 : ms;
        return r < 0 ? r : 1;
    }
}

/* ── audio ──────────────────────────────────────────────────────────────────────────────────────────────────────── */
static int link_filter(AVFilterGraph *g, AVFilterContext **last, const char *name, const char *args)
{
    AVFilterContext *c = NULL;
    int r = avfilter_graph_create_filter(&c, avfilter_get_by_name(name), name, args, NULL, g);
    if (r < 0) return r;
    r = avfilter_link(*last, 0, c, 0);
    if (r < 0) return r;
    *last = c;
    return 0;
}

static int build_graph(tgav *t)
{
    AVCodecContext *c = t->adec;
    AVStream *st = t->fmt->streams[t->aidx];
    AVChannelLayout in_layout;
    AVFilterContext *last;
    char args[512], lay[128], arg2[256];
    const char *out_lay = t->out_ch == 1 ? "mono" : "stereo";
    int r;
    free_graph(t);
    t->graph = avfilter_graph_alloc();
    if (!t->graph) return AVERROR(ENOMEM);
    memset(&in_layout, 0, sizeof(in_layout));
    if (c->ch_layout.order == AV_CHANNEL_ORDER_UNSPEC || c->ch_layout.nb_channels <= 0)
        av_channel_layout_default(&in_layout, c->ch_layout.nb_channels > 0 ? c->ch_layout.nb_channels : 2);
    else av_channel_layout_copy(&in_layout, &c->ch_layout);
    av_channel_layout_describe(&in_layout, lay, sizeof(lay));
    av_channel_layout_uninit(&in_layout);
    snprintf(args, sizeof(args), "time_base=%d/%d:sample_rate=%d:sample_fmt=%s:channel_layout=%s",
             st->time_base.num, st->time_base.den, c->sample_rate, av_get_sample_fmt_name(c->sample_fmt), lay);
    r = avfilter_graph_create_filter(&t->src, avfilter_get_by_name("abuffer"), "in", args, NULL, t->graph);
    if (r < 0) return r;
    r = avfilter_graph_create_filter(&t->sink, avfilter_get_by_name("abuffersink"), "out", NULL, NULL, t->graph);
    if (r < 0) return r;
    last = t->src;
    if (t->tempo_pct != 100) {
        snprintf(arg2, sizeof(arg2), "tempo=%d.%02d", t->tempo_pct / 100, t->tempo_pct % 100);
        if ((r = link_filter(t->graph, &last, "atempo", arg2)) < 0) return r;
    }
    snprintf(arg2, sizeof(arg2), "osr=%d:ochl=%s:osf=s16", t->out_rate, out_lay);
    if ((r = link_filter(t->graph, &last, "aresample", arg2)) < 0) return r;
    snprintf(arg2, sizeof(arg2), "sample_fmts=s16:sample_rates=%d:channel_layouts=%s", t->out_rate, out_lay);
    if ((r = link_filter(t->graph, &last, "aformat", arg2)) < 0) return r;
    if ((r = avfilter_link(last, 0, t->sink, 0)) < 0) return r;
    if ((r = avfilter_graph_config(t->graph, NULL)) < 0) return r;
    t->graph_dirty = 0;
    t->a_graph_flushed = 0;
    t->a_have_base = 0;
    av_frame_unref(t->afilt);
    t->afilt_off = 0;
    return 0;
}

TGAV_API int TGAV_CALL tgav_set_audio_format(tgav *t, int sample_rate, int channels)
{
    if (!t || sample_rate < 8000 || sample_rate > 192000 || channels < 1 || channels > 2) return AVERROR(EINVAL);
    t->out_rate = sample_rate;
    t->out_ch = channels;
    t->graph_dirty = 1;
    return 0;
}

TGAV_API int TGAV_CALL tgav_set_tempo(tgav *t, int tempo_percent)
{
    if (!t || tempo_percent < 50 || tempo_percent > 400) return AVERROR(EINVAL);
    if (t->tempo_pct == tempo_percent) return 0;
    t->tempo_pct = tempo_percent;
    t->graph_dirty = 1;   /* rebuilt at the next read; what the old graph still held is dropped (a few ms) */
    return 0;
}

/* Push the next decoded audio frame (or the end) into the graph. 1 = pushed, 0 = nothing more, < 0 = error. */
static int feed_graph(tgav *t)
{
    AVStream *st = t->fmt->streams[t->aidx];
    for (;;) {
        int r, ms;
        if (t->a_graph_flushed) return 0;
        r = decode_next(t, t->adec, &t->aq, &t->a_flushed, t->aframe);
        if (r < 0) return r;
        if (r == 0) {
            r = av_buffersrc_add_frame_flags(t->src, NULL, 0);   /* end: the graph drains what it holds */
            if (r < 0) return r;
            t->a_graph_flushed = 1;
            return 1;
        }
        ms = ms_of(t, st, t->aframe->best_effort_timestamp != AV_NOPTS_VALUE ? t->aframe->best_effort_timestamp : t->aframe->pts);
        if (t->a_skip_ms >= 0) {
            int end = ms + (int)((int64_t)t->aframe->nb_samples * 1000 / (t->adec->sample_rate > 0 ? t->adec->sample_rate : 48000));
            if (ms >= 0 && end <= t->a_skip_ms) { av_frame_unref(t->aframe); continue; }   /* ends before the seek target */
            t->a_skip_ms = -1;
        }
        if (!t->a_have_base) { t->a_base_in_ms = ms < 0 ? 0 : ms; t->a_base_out_ms = -1; t->a_have_base = 1; }
        r = av_buffersrc_add_frame_flags(t->src, t->aframe, 0);   /* takes the frame's reference */
        av_frame_unref(t->aframe);
        if (r < 0) return r;
        return 1;
    }
}

TGAV_API int TGAV_CALL tgav_read_audio(tgav *t, int16_t *dst, int max_samples, int *pts_ms)
{
    int written = 0, r;
    if (!t || t->aidx < 0 || !dst || max_samples <= 0) return AVERROR(EINVAL);
    if (t->graph_dirty || !t->graph) { r = build_graph(t); if (r < 0) return r; }
    while (written < max_samples) {
        if (t->afilt->nb_samples > 0 && t->afilt_off < t->afilt->nb_samples) {
            int n = t->afilt->nb_samples - t->afilt_off;
            if (n > max_samples - written) n = max_samples - written;
            if (written == 0 && pts_ms) {
                AVRational tb = av_buffersink_get_time_base(t->sink);
                int out_ms = t->afilt->pts == AV_NOPTS_VALUE ? 0
                           : (int)(av_rescale_q(t->afilt->pts, tb, (AVRational){1, 1000}) - t->start_us / 1000)
                             + (int)((int64_t)t->afilt_off * 1000 / t->out_rate);
                if (t->a_base_out_ms < 0) t->a_base_out_ms = out_ms;
                *pts_ms = t->a_base_in_ms + (int)((int64_t)(out_ms - t->a_base_out_ms) * t->tempo_pct / 100);
            }
            memcpy(dst + (size_t)written * t->out_ch,
                   t->afilt->data[0] + (size_t)t->afilt_off * t->out_ch * 2, (size_t)n * t->out_ch * 2);
            written += n;
            t->afilt_off += n;
            continue;
        }
        av_frame_unref(t->afilt);
        t->afilt_off = 0;
        r = av_buffersink_get_frame(t->sink, t->afilt);
        if (r == 0) continue;
        if (r == AVERROR_EOF) break;
        if (r != AVERROR(EAGAIN)) return r;
        r = feed_graph(t);
        if (r < 0) return r;
        if (r == 0) break;
    }
    return written;
}

/* ── camera frames (1.1) ─────────────────────────────────────────────────────────────────────────────────────────── */
struct tgav_scaler { struct SwsContext *sws; };

TGAV_API tgav_scaler *TGAV_CALL tgav_scaler_create(void) { return (tgav_scaler *)av_mallocz(sizeof(tgav_scaler)); }

TGAV_API void TGAV_CALL tgav_scaler_free(tgav_scaler *s)
{
    if (!s) return;
    if (s->sws) sws_freeContext(s->sws);
    av_free(s);
}

TGAV_API int TGAV_CALL tgav_scale(tgav_scaler *s, int fmt, const uint8_t *plane0, int stride0, const uint8_t *plane1, int stride1,
                                  int src_w, int src_h, int crop_x, int crop_y, int crop_w, int crop_h,
                                  uint8_t *dst, int dst_stride, int dst_w, int dst_h, int mirror)
{
    const uint8_t *src[4] = { NULL, NULL, NULL, NULL };
    int ls[4] = { 0, 0, 0, 0 };
    uint8_t *d[4] = { NULL, NULL, NULL, NULL };
    int dls[4] = { 0, 0, 0, 0 };
    enum AVPixelFormat pf;
    int y;
    if (!s || !plane0 || !dst || dst_w <= 0 || dst_h <= 0 || dst_stride < dst_w * 4) return AVERROR(EINVAL);
    crop_x &= ~1; crop_y &= ~1;   /* chroma is shared by 2x2 (NV12) or 2x1 (YUY2) pixels */
    if (crop_x < 0 || crop_y < 0 || crop_w < 2 || crop_h < 2 || crop_x + crop_w > src_w || crop_y + crop_h > src_h) return AVERROR(EINVAL);
    switch (fmt) {
    case TGAV_PIX_NV12:
        if (!plane1) return AVERROR(EINVAL);
        pf = AV_PIX_FMT_NV12;
        src[0] = plane0 + (size_t)crop_y * stride0 + crop_x; ls[0] = stride0;
        src[1] = plane1 + (size_t)(crop_y / 2) * stride1 + crop_x; ls[1] = stride1;
        break;
    case TGAV_PIX_YUY2:
        pf = AV_PIX_FMT_YUYV422;
        src[0] = plane0 + (size_t)crop_y * stride0 + (size_t)crop_x * 2; ls[0] = stride0;
        break;
    case TGAV_PIX_BGRA:
        pf = AV_PIX_FMT_BGRA;
        src[0] = plane0 + (size_t)crop_y * stride0 + (size_t)crop_x * 4; ls[0] = stride0;
        break;
    default:
        return AVERROR(EINVAL);
    }
    s->sws = sws_getCachedContext(s->sws, crop_w, crop_h, pf, dst_w, dst_h, AV_PIX_FMT_BGRA, SWS_BILINEAR, NULL, NULL, NULL);
    if (!s->sws) return AVERROR(EINVAL);
    d[0] = dst; dls[0] = dst_stride;
    sws_scale(s->sws, src, ls, 0, crop_h, d, dls);
    if (mirror)
        for (y = 0; y < dst_h; y++) {
            uint32_t *row = (uint32_t *)(dst + (size_t)y * dst_stride);
            int a = 0, b = dst_w - 1;
            while (a < b) { uint32_t tmp = row[a]; row[a++] = row[b]; row[b--] = tmp; }
        }
    return 0;
}

/* ── seek ───────────────────────────────────────────────────────────────────────────────────────────────────────── */
TGAV_API int TGAV_CALL tgav_seek(tgav *t, int ms)
{
    int64_t ts;
    int r;
    if (!t) return AVERROR(EINVAL);
    if (ms < 0) ms = 0;
    ts = (int64_t)ms * 1000 + t->start_us;
    EnterCriticalSection(&t->lock);
    r = avformat_seek_file(t->fmt, -1, INT64_MIN, ts, ts, 0);   /* the keyframe at or before ts */
    if (r < 0) r = av_seek_frame(t->fmt, -1, ts, AVSEEK_FLAG_BACKWARD);
    q_clear(&t->vq);
    q_clear(&t->aq);
    t->demux_eof = 0;
    LeaveCriticalSection(&t->lock);
    if (t->vdec) { avcodec_flush_buffers(t->vdec); t->v_flushed = 0; t->v_skip_ms = ms; }
    if (t->adec) { avcodec_flush_buffers(t->adec); t->a_flushed = 0; t->a_skip_ms = ms; t->graph_dirty = 1; }
    return r < 0 ? r : 0;
}
