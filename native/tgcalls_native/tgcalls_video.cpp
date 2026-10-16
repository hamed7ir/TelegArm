// tgcalls_native video: our own tgcalls platform (the link replaces tdesktop's DesktopInterface, whose camera is
// DirectShow), push-style cameras, the remote-video sink, the codec policy and the video self-test. Same compiler
// settings as tgcalls_native.cpp (the BUILD-INFO defines are ABI-relevant).
#include <winsock2.h>
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "tgcalls/Instance.h"
#include "tgcalls/PlatformContext.h"
#include "tgcalls/StaticThreads.h"
#include "tgcalls/VideoCaptureInterface.h"
#include "tgcalls/VideoCapturerInterface.h"
#include "tgcalls/platform/PlatformInterface.h"

#include "api/video/i420_buffer.h"
#include "api/video/video_frame.h"
#include "api/video/video_rotation.h"
#include "api/video/video_sink_interface.h"
#include "api/video_codecs/builtin_video_decoder_factory.h"
#include "api/video_codecs/builtin_video_encoder_factory.h"
#include "api/video_codecs/video_encoder.h"
#include "api/video_codecs/video_encoder_factory.h"
#include "media/base/adapted_video_track_source.h"
#include "media/base/media_constants.h"
#include "pc/video_track_source_proxy.h"
#include "rtc_base/time_utils.h"

#include "libyuv.h"

#include "tgcalls_internal.h"

namespace {

std::atomic<bool> g_h264Encode{true};
std::atomic<int> g_encVp8{0}, g_encVp9{0}, g_encH264{0}, g_encOther{0};

// Scales w x h down (never up) to fit max_w x max_h, keeping the shape; even sizes (I420 chroma). 0 = no limit.
void Fit(int w, int h, int max_w, int max_h, int *ow, int *oh) {
  double s = 1.0;
  if (max_w > 0 && w > max_w) s = std::min(s, (double)max_w / w);
  if (max_h > 0 && h > max_h) s = std::min(s, (double)max_h / h);
  int x = (int)(w * s), y = (int)(h * s);
  if (s < 1.0) { x &= ~1; y &= ~1; }
  *ow = std::max(2, x);
  *oh = std::max(2, y);
}

webrtc::VideoRotation RotationOf(int degrees) {
  switch (((degrees % 360) + 360) % 360) {
    case 90: return webrtc::kVideoRotation_90;
    case 180: return webrtc::kVideoRotation_180;
    case 270: return webrtc::kVideoRotation_270;
    default: return webrtc::kVideoRotation_0;
  }
}

// ── the outgoing source ──────────────────────────────────────────────────────────────────────────────────────────────
// Pictures come from outside (the app's camera thread). AdaptFrame first: it is false with no sink (no call sending) and
// when WebRTC's adapter sheds load (CPU overuse on the Tegra) - the cheapest place to drop a frame.
class PushSource : public rtc::AdaptedVideoTrackSource {   // not final: make_ref_counted derives from it
 public:
  SourceState state() const override { return kLive; }
  bool remote() const override { return false; }
  bool is_screencast() const override { return false; }
  absl::optional<bool> needs_denoising() const override { return false; }

  bool Deliver(const rtc::scoped_refptr<webrtc::I420Buffer> &buf, int64_t time_us) {
    int ow = 0, oh = 0, cw = 0, ch = 0, cx = 0, cy = 0;
    if (!AdaptFrame(buf->width(), buf->height(), time_us, &ow, &oh, &cw, &ch, &cx, &cy)) return false;
    rtc::scoped_refptr<webrtc::VideoFrameBuffer> out = buf;
    if (cw != buf->width() || ch != buf->height() || ow != cw || oh != ch) {
      auto scaled = webrtc::I420Buffer::Create(ow, oh);
      scaled->CropAndScaleFrom(*buf, cx, cy, cw, ch);
      out = scaled;
    }
    OnFrame(webrtc::VideoFrame::Builder()
                .set_video_frame_buffer(out)
                .set_timestamp_us(time_us)
                .set_rotation(webrtc::kVideoRotation_0)
                .build());
    return true;
  }
};

// What a tgn_camera and its capturer share: the source, known once the media thread built it.
struct CameraLink {
  std::mutex m;
  rtc::scoped_refptr<PushSource> source;
};

class CameraContext final : public tgcalls::PlatformContext {
 public:
  explicit CameraContext(std::weak_ptr<CameraLink> link) : link(std::move(link)) {}
  std::weak_ptr<CameraLink> link;
};

class PushCapturer final : public tgcalls::VideoCapturerInterface {
 public:
  PushCapturer(webrtc::scoped_refptr<webrtc::VideoTrackSourceInterface> source, std::function<void(tgcalls::VideoState)> stateUpdated,
               std::weak_ptr<CameraLink> link)
      : source_(std::move(source)), stateUpdated_(std::move(stateUpdated)), link_(std::move(link)) {}
  ~PushCapturer() override {
    if (auto link = link_.lock()) {
      std::lock_guard<std::mutex> lock(link->m);
      link->source = nullptr;
    }
  }
  void setState(tgcalls::VideoState state) override {
    if (stateUpdated_) stateUpdated_(state);   // V1 sends this to the peer (MediaManager's setStateUpdated)
  }
  void setPreferredCaptureAspectRatio(float) override {}
  void setUncroppedOutput(std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>> sink) override {
    if (uncropped_) source_->RemoveSink(uncropped_.get());
    uncropped_ = std::move(sink);
    if (uncropped_) source_->AddOrUpdateSink(uncropped_.get(), rtc::VideoSinkWants());
  }
  int getRotation() override { return 0; }   // pictures are rotated upright before they are pushed

 private:
  webrtc::scoped_refptr<webrtc::VideoTrackSourceInterface> source_;
  std::function<void(tgcalls::VideoState)> stateUpdated_;
  std::weak_ptr<CameraLink> link_;
  std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>> uncropped_;
};

// ── codecs ───────────────────────────────────────────────────────────────────────────────────────────────────────────
// The built-in factories, with H.264 ENCODING optionally removed: OpenH264 in these packages has no SIMD on ARM32, libvpx
// has NEON. Decoding is untouched, so a peer may still send us H.264. Counts the encoders made (the self-test asks).
// Removed ONLY for the V2 1:1 instances (the ones asking with preferHardwareEncoding = true): V2 sends H.264 whenever it
// is offered. V1 picks its send codec through supportsEncoding (H.264 off there), and a GROUP call builds its RECEIVE
// codec list from this factory (GroupInstanceCustomImpl.cpp:2306): hiding H.264 there would stop us receiving it - the
// group call is told to prefer VP8 for sending instead (videoCodecPreferences).
bool IsH264(const webrtc::SdpVideoFormat &f) { return f.name == cricket::kH264CodecName; }

class EncoderFactory final : public webrtc::VideoEncoderFactory {
 public:
  explicit EncoderFactory(bool h264) : inner_(webrtc::CreateBuiltinVideoEncoderFactory()), h264_(h264) {}
  std::vector<webrtc::SdpVideoFormat> GetSupportedFormats() const override { return Filter(inner_->GetSupportedFormats()); }
  std::vector<webrtc::SdpVideoFormat> GetImplementations() const override { return Filter(inner_->GetImplementations()); }
  CodecSupport QueryCodecSupport(const webrtc::SdpVideoFormat &format, absl::optional<std::string> mode) const override {
    if (!h264_ && IsH264(format)) return CodecSupport();
    return inner_->QueryCodecSupport(format, std::move(mode));
  }
  std::unique_ptr<webrtc::VideoEncoder> CreateVideoEncoder(const webrtc::SdpVideoFormat &format) override {
    if (!h264_ && IsH264(format)) return nullptr;
    if (format.name == cricket::kVp8CodecName) g_encVp8++;
    else if (format.name == cricket::kVp9CodecName) g_encVp9++;
    else if (IsH264(format)) g_encH264++;
    else g_encOther++;
    tgn::Log("tgn: video encoder " + format.name);
    return inner_->CreateVideoEncoder(format);
  }
  std::unique_ptr<EncoderSelectorInterface> GetEncoderSelector() const override { return inner_->GetEncoderSelector(); }

 private:
  std::vector<webrtc::SdpVideoFormat> Filter(std::vector<webrtc::SdpVideoFormat> v) const {
    if (!h264_) v.erase(std::remove_if(v.begin(), v.end(), IsH264), v.end());
    return v;
  }
  std::unique_ptr<webrtc::VideoEncoderFactory> inner_;
  bool h264_;
};

// ── the platform ─────────────────────────────────────────────────────────────────────────────────────────────────────
// DesktopInterface (tdesktop) minus its DirectShow camera and screen capture; the rest as it does.
class Platform final : public tgcalls::PlatformInterface {
 public:
  std::unique_ptr<webrtc::VideoEncoderFactory> makeVideoEncoderFactory(bool preferHardwareEncoding, bool) override {
    return std::make_unique<EncoderFactory>(g_h264Encode.load() || !preferHardwareEncoding);   // see EncoderFactory
  }
  std::unique_ptr<webrtc::VideoDecoderFactory> makeVideoDecoderFactory() override {
    return webrtc::CreateBuiltinVideoDecoderFactory();
  }
  bool supportsEncoding(const std::string &name) override {   // V1's codec list (CodecSelectHelper)
    return (name == cricket::kH264CodecName && g_h264Encode.load()) || name == cricket::kVp8CodecName;
  }
  webrtc::scoped_refptr<webrtc::VideoTrackSourceInterface> makeVideoSource(rtc::Thread *signaling, rtc::Thread *worker) override {
    return webrtc::VideoTrackSourceProxy::Create(signaling, worker, rtc::make_ref_counted<PushSource>());
  }
  void adaptVideoSource(webrtc::scoped_refptr<webrtc::VideoTrackSourceInterface>, int, int, int) override {}
  std::unique_ptr<tgcalls::VideoCapturerInterface> makeVideoCapturer(
      webrtc::scoped_refptr<webrtc::VideoTrackSourceInterface> source, std::string deviceId,
      std::function<void(tgcalls::VideoState)> stateUpdated, std::function<void(tgcalls::PlatformCaptureInfo)>,
      std::shared_ptr<tgcalls::PlatformContext> platformContext, std::pair<int, int> &outResolution) override {
    std::weak_ptr<CameraLink> weak;
    if (auto *ctx = dynamic_cast<CameraContext *>(platformContext.get())) weak = ctx->link;
    if (auto link = weak.lock()) {
      // tdesktop's GetSink pattern: the proxy's internal() is the PushSource made by makeVideoSource
      auto *proxy = static_cast<webrtc::VideoTrackSourceProxy *>(source.get());
      std::lock_guard<std::mutex> lock(link->m);
      link->source = rtc::scoped_refptr<PushSource>(static_cast<PushSource *>(proxy->internal()));
    } else {
      tgn::Log("tgn: a video capture without a tgn_camera (device '" + deviceId + "'): it sends nothing");
    }
    outResolution = {640, 480};
    return std::make_unique<PushCapturer>(std::move(source), std::move(stateUpdated), weak);
  }
};

}  // namespace

namespace tgcalls {
std::unique_ptr<PlatformInterface> CreatePlatformInterface() { return std::make_unique<Platform>(); }
}  // namespace tgcalls

// ── cameras ──────────────────────────────────────────────────────────────────────────────────────────────────────────
namespace tgn {

class CameraCore {
 public:
  CameraCore(int max_w, int max_h) : maxW(max_w), maxH(max_h) {}

  std::shared_ptr<CameraLink> link = std::make_shared<CameraLink>();
  std::shared_ptr<tgcalls::VideoCaptureInterface> capture;
  int maxW, maxH;

  std::mutex m;   // one push at a time; guards the preview settings (no preview callback after set_preview returns)
  bool closed = false;
  tgn_frame_fn previewCb = nullptr;
  void *previewUser = nullptr;
  int previewW = 0, previewH = 0, previewFps = 15;
  bool previewMirror = true;
  int64_t lastPreviewUs = 0, lastUs = 0;
  std::vector<uint8_t> previewBgra;

  std::atomic<int> pushed{0}, sent{0}, dropped{0}, previews{0}, lastW{0}, lastH{0};
};

}  // namespace tgn

struct tgn_camera {
  std::shared_ptr<tgn::CameraCore> core;
};

namespace {

// Any accepted input -> an upright I420 buffer no larger than max. nullptr on bad input.
rtc::scoped_refptr<webrtc::I420Buffer> ToI420(const tgn_frame_in &f, int max_w, int max_h) {
  const int w = f.width, h = f.height;
  if (w < 2 || h < 2 || w > 8192 || h > 8192 || !f.plane0) return nullptr;
  auto buf = webrtc::I420Buffer::Create(w, h);
  int rc = -1;
  switch (f.format) {
    case TGN_PIX_NV12:
      if (!f.plane1 || (w & 1) || (h & 1)) return nullptr;
      rc = libyuv::NV12ToI420(f.plane0, f.stride0, f.plane1, f.stride1, buf->MutableDataY(), buf->StrideY(),
                              buf->MutableDataU(), buf->StrideU(), buf->MutableDataV(), buf->StrideV(), w, h);
      break;
    case TGN_PIX_YUY2:
      if (w & 1) return nullptr;
      rc = libyuv::YUY2ToI420(f.plane0, f.stride0, buf->MutableDataY(), buf->StrideY(), buf->MutableDataU(), buf->StrideU(),
                              buf->MutableDataV(), buf->StrideV(), w, h);
      break;
    case TGN_PIX_BGRA:
      rc = libyuv::ARGBToI420(f.plane0, f.stride0, buf->MutableDataY(), buf->StrideY(), buf->MutableDataU(), buf->StrideU(),
                              buf->MutableDataV(), buf->StrideV(), w, h);
      break;
    case TGN_PIX_I420:
      if (!f.plane1 || !f.plane2) return nullptr;
      rc = libyuv::I420Copy(f.plane0, f.stride0, f.plane1, f.stride1, f.plane2, f.stride2, buf->MutableDataY(), buf->StrideY(),
                            buf->MutableDataU(), buf->StrideU(), buf->MutableDataV(), buf->StrideV(), w, h);
      break;
    default:
      return nullptr;
  }
  if (rc != 0) return nullptr;
  const auto rotation = RotationOf(f.rotation);
  if (rotation != webrtc::kVideoRotation_0) buf = webrtc::I420Buffer::Rotate(*buf, rotation);
  int ow = 0, oh = 0;
  Fit(buf->width(), buf->height(), max_w, max_h, &ow, &oh);
  if (ow != buf->width() || oh != buf->height()) {
    auto scaled = webrtc::I420Buffer::Create(ow, oh);
    scaled->ScaleFrom(*buf);
    buf = scaled;
  }
  return buf;
}

// I420 -> BGRA fitted in max (and mirrored if asked) into out. Returns the size through w/h.
void ToBgra(const webrtc::I420BufferInterface &src, int max_w, int max_h, bool mirror, std::vector<uint8_t> &out, int *w, int *h) {
  int ow = 0, oh = 0;
  Fit(src.width(), src.height(), max_w, max_h, &ow, &oh);
  rtc::scoped_refptr<webrtc::I420Buffer> scaled;
  const webrtc::I420BufferInterface *p = &src;
  if (ow != src.width() || oh != src.height()) {
    scaled = webrtc::I420Buffer::Create(ow, oh);
    scaled->ScaleFrom(src);
    p = scaled.get();
  }
  out.resize((size_t)ow * oh * 4);
  libyuv::I420ToARGB(p->DataY(), p->StrideY(), p->DataU(), p->StrideU(), p->DataV(), p->StrideV(), out.data(), ow * 4, ow, oh);
  if (mirror) {
    std::vector<uint8_t> row((size_t)ow * 4);
    for (int y = 0; y < oh; ++y) {
      uint8_t *line = out.data() + (size_t)y * ow * 4;
      libyuv::ARGBMirror(line, ow * 4, row.data(), ow * 4, ow, 1);
      std::memcpy(line, row.data(), row.size());
    }
  }
  *w = ow;
  *h = oh;
}

}  // namespace

namespace tgn {

std::shared_ptr<CameraCore> CoreOf(tgn_camera *cam) { return cam ? cam->core : nullptr; }

std::shared_ptr<tgcalls::VideoCaptureInterface> CaptureOf(tgn_camera *cam) {
  return cam && cam->core ? cam->core->capture : nullptr;
}

int Push(CameraCore &core, const tgn_frame_in &f) {
  std::lock_guard<std::mutex> lock(core.m);
  if (core.closed) return 1;
  core.pushed++;
  auto buf = ToI420(f, core.maxW, core.maxH);
  if (!buf) {
    core.dropped++;
    return -2;
  }
  int64_t t = rtc::TimeMicros();   // frames whose time is not newer than the last are dropped by the encoder
  if (t <= core.lastUs) t = core.lastUs + 1;
  core.lastUs = t;
  // at most previewFps a second; 10 % slack, or a 30 fps camera (33 ms apart) yields 12 of 15 (measured)
  if (core.previewCb && t - core.lastPreviewUs >= 900000 / std::max(1, core.previewFps)) {
    core.lastPreviewUs = t;
    int pw = 0, ph = 0;
    ToBgra(*buf, core.previewW, core.previewH, core.previewMirror, core.previewBgra, &pw, &ph);
    core.previews++;
    core.previewCb(core.previewUser, core.previewBgra.data(), pw, ph, pw * 4);
  }
  rtc::scoped_refptr<PushSource> source;
  {
    std::lock_guard<std::mutex> l(core.link->m);
    source = core.link->source;
  }
  if (source && source->Deliver(buf, t)) {
    core.sent++;
    core.lastW = buf->width();
    core.lastH = buf->height();
    return 0;
  }
  core.dropped++;
  return 1;
}

// ── remote video ─────────────────────────────────────────────────────────────────────────────────────────────────────
class RemoteVideo final : public rtc::VideoSinkInterface<webrtc::VideoFrame> {
 public:
  // On WebRTC's "IncomingVideoStream" queue; its sink lock is held, so this only converts and hands over.
  void OnFrame(const webrtc::VideoFrame &frame) override {
    frames++;
    width = frame.width();
    height = frame.height();
    rotation = (int)frame.rotation();
    std::lock_guard<std::mutex> lock(m);
    if (!cb) return;
    if (maxFps > 0) {   // a group tile: skip before converting (the Tegra decodes several streams at once)
      int64_t now = rtc::TimeMicros();
      if (now - lastUs < 900000 / maxFps) return;
      lastUs = now;
    }
    auto i420 = frame.video_frame_buffer()->ToI420();
    if (!i420) return;
    rtc::scoped_refptr<webrtc::I420BufferInterface> upright = i420;
    if (frame.rotation() != webrtc::kVideoRotation_0) upright = webrtc::I420Buffer::Rotate(*i420, frame.rotation());
    int w = 0, h = 0;
    ToBgra(*upright, maxW, maxH, false, bgra, &w, &h);
    cb(user, bgra.data(), w, h, w * 4);
  }

  std::mutex m;
  tgn_frame_fn cb = nullptr;
  void *user = nullptr;
  int maxW = 0, maxH = 0, maxFps = 0;
  int64_t lastUs = 0;
  std::vector<uint8_t> bgra;
  std::atomic<int> frames{0}, width{0}, height{0}, rotation{0};
};

std::shared_ptr<RemoteVideo> MakeRemoteVideo() { return std::make_shared<RemoteVideo>(); }

void AttachRemoteVideo(const std::shared_ptr<RemoteVideo> &remote, tgcalls::Instance &instance) {
  instance.setIncomingVideoOutput(remote);   // tgcalls keeps a weak_ptr: the tgn_call holds the strong one
}

void SetRemoteVideo(RemoteVideo &remote, tgn_frame_fn cb, void *user, int max_w, int max_h, int max_fps) {
  std::lock_guard<std::mutex> lock(remote.m);
  remote.cb = cb;
  remote.user = user;
  remote.maxW = max_w;
  remote.maxH = max_h;
  remote.maxFps = max_fps;
}

std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>> SinkOf(const std::shared_ptr<RemoteVideo> &remote) { return remote; }

void RemoteVideoStats(RemoteVideo &remote, tgn_video_stats *out) {
  out->remote_frames = remote.frames;
  out->remote_width = remote.width;
  out->remote_height = remote.height;
  out->remote_rotation = remote.rotation;
}

}  // namespace tgn

// ── the C ABI ────────────────────────────────────────────────────────────────────────────────────────────────────────
extern "C" {

TGN_API tgn_camera *TGN_CALL tgn_camera_create(int32_t max_w, int32_t max_h) {
  tgn::EnsureInit();
  try {
    auto core = std::make_shared<tgn::CameraCore>(max_w > 0 ? max_w : 1280, max_h > 0 ? max_h : 720);
    // the object is built on tgcalls' media thread (asynchronously): pushes before that are dropped
    core->capture = tgcalls::VideoCaptureInterface::Create(tgcalls::StaticThreads::getThreads(), "tgn-camera", false,
                                                           std::make_shared<CameraContext>(core->link));
    if (!core->capture) return nullptr;
    auto *cam = new tgn_camera;
    cam->core = std::move(core);
    return cam;
  } catch (const std::exception &e) {
    tgn::Log(std::string("tgn: camera create failed: ") + e.what());
    return nullptr;
  }
}

TGN_API int TGN_CALL tgn_camera_push(tgn_camera *cam, const tgn_frame_in *f) {
  if (!cam || !cam->core || !f) return -1;
  return tgn::Push(*cam->core, *f);
}

TGN_API void TGN_CALL tgn_camera_set_preview(tgn_camera *cam, tgn_frame_fn cb, void *user, int32_t max_w, int32_t max_h, int32_t fps,
                                             int32_t mirror) {
  if (!cam || !cam->core) return;
  std::lock_guard<std::mutex> lock(cam->core->m);
  cam->core->previewCb = cb;
  cam->core->previewUser = user;
  cam->core->previewW = max_w;
  cam->core->previewH = max_h;
  cam->core->previewFps = fps > 0 ? fps : 15;
  cam->core->previewMirror = mirror != 0;
  cam->core->lastPreviewUs = 0;
}

TGN_API void TGN_CALL tgn_camera_destroy(tgn_camera *cam) {
  if (!cam) return;
  std::shared_ptr<tgcalls::VideoCaptureInterface> capture;
  if (cam->core) {
    std::lock_guard<std::mutex> lock(cam->core->m);
    cam->core->closed = true;   // a media sink may hold the core a little longer: its samples are now dropped
    cam->core->previewCb = nullptr;
    capture = std::move(cam->core->capture);
  }
  delete cam;
  capture.reset();      // calls still sending it keep their own reference
  tgn::Drain(nullptr);  // a capture's teardown is posted to the media thread: finish it now (see Drain)
}

TGN_API int TGN_CALL tgn_camera_stats_get(tgn_camera *cam, tgn_camera_stats *out) {
  if (!cam || !cam->core || !out) return -1;
  auto &c = *cam->core;
  out->pushed = c.pushed;
  out->sent = c.sent;
  out->dropped = c.dropped;
  out->previews = c.previews;
  out->width = c.lastW;
  out->height = c.lastH;
  return 0;
}

TGN_API void TGN_CALL tgn_set_option(int32_t option, int32_t value) {
  if (option == TGN_OPT_H264_ENCODE) g_h264Encode = value != 0;
}

TGN_API int TGN_CALL tgn_encoders(char *buf, int32_t len) {
  char s[128];
  int n = std::snprintf(s, sizeof(s), "VP8 %d, H264 %d, VP9 %d, other %d", g_encVp8.load(), g_encH264.load(), g_encVp9.load(),
                        g_encOther.load());
  if (buf && len > 0) {
    int k = std::min(n, (int)len - 1);
    std::memcpy(buf, s, k);
    buf[k] = 0;
  }
  return n;
}

// ── the video self-test ──────────────────────────────────────────────────────────────────────────────────────────────
namespace {

bool IsRed(const uint8_t *p) { return p[2] > 150 && p[1] < 100 && p[0] < 100; }    // B,G,R,A
bool IsBlue(const uint8_t *p) { return p[0] > 150 && p[2] < 100 && p[1] < 110; }

struct Watch {   // what a picture callback saw: count, size, and the colours at quarter points of the lower half
  std::atomic<int> frames{0}, good{0}, w{0}, h{0};
  bool expectRedLeft = true;
  static void TGN_CALL On(void *user, const uint8_t *bgra, int32_t w, int32_t h, int32_t stride) {
    auto *self = (Watch *)user;
    self->frames++;
    self->w = w;
    self->h = h;
    const uint8_t *left = bgra + (size_t)(h * 3 / 4) * stride + (size_t)(w / 4) * 4;
    const uint8_t *right = bgra + (size_t)(h * 3 / 4) * stride + (size_t)(w * 3 / 4) * 4;
    bool ok = self->expectRedLeft ? (IsRed(left) && IsBlue(right)) : (IsBlue(left) && IsRed(right));
    if (ok) self->good++;
  }
};

struct Side {
  tgn_call *call = nullptr;
  Side *peer = nullptr;
  std::atomic<int> state{-1}, remoteVideo{-1};
  std::mutex m;
  std::vector<std::vector<uint8_t>> inbox;
};

void TGN_CALL OnState(void *u, int32_t s) { ((Side *)u)->state = s; }
void TGN_CALL OnMedia(void *u, int32_t, int32_t video) { ((Side *)u)->remoteVideo = video; }
void TGN_CALL OnSignal(void *u, const uint8_t *d, int32_t n) {
  auto *self = (Side *)u;
  std::lock_guard<std::mutex> lock(self->peer->m);
  self->peer->inbox.emplace_back(d, d + n);
}
void Deliver(Side &s) {
  std::vector<std::vector<uint8_t>> msgs;
  {
    std::lock_guard<std::mutex> lock(s.m);
    msgs.swap(s.inbox);
  }
  for (auto &m : msgs) tgn_call_receive_signaling(s.call, m.data(), (int32_t)m.size());
}

// 640x480 NV12: left half red, right half blue (BT.601 limited), a white square moving along the top band.
void Pattern(std::vector<uint8_t> &nv12, int w, int h, int i) {
  nv12.resize((size_t)w * h * 3 / 2);
  uint8_t *y = nv12.data(), *uv = nv12.data() + (size_t)w * h;
  for (int r = 0; r < h; ++r)
    for (int c = 0; c < w; ++c) y[(size_t)r * w + c] = c < w / 2 ? 81 : 41;
  for (int r = 0; r < h / 2; ++r)
    for (int c = 0; c < w / 2; ++c) {
      uint8_t *p = uv + (size_t)r * w + (size_t)c * 2;
      p[0] = c < w / 4 ? 90 : 240;
      p[1] = c < w / 4 ? 240 : 110;
    }
  int sx = (i * 8) % (w - 64), sy = 32;
  for (int r = sy; r < sy + 64; ++r)
    for (int c = sx; c < sx + 64; ++c) y[(size_t)r * w + c] = 235;
  for (int r = sy / 2; r < (sy + 64) / 2; ++r)
    for (int c = sx / 2; c < (sx + 64) / 2; ++c) {
      uv[(size_t)r * w + (size_t)c * 2] = 128;
      uv[(size_t)r * w + (size_t)c * 2 + 1] = 128;
    }
}

}  // namespace

TGN_API int TGN_CALL tgn_selftest_video(const char *version, int32_t seconds, int32_t flags, char *report, int32_t len) {
  tgn::EnsureInit();
  const bool h264Before = g_h264Encode.load();
  if (flags & TGN_SELFTEST_NO_H264) g_h264Encode = false;
  char before[128];
  tgn_encoders(before, sizeof(before));

  std::string ver;
  if (version && *version) {
    ver = version;
  } else {
    char versions[256];
    tgn_versions(versions, sizeof(versions));
    ver = std::string(versions).substr(0, std::string(versions).find(';'));
  }
  std::string r = "version " + ver + (flags & TGN_SELFTEST_NO_H264 ? ", H.264 encoding off" : "") + "\n";

  tgn_camera *cam = tgn_camera_create(1280, 720);
  Watch preview, remote;
  preview.expectRedLeft = false;   // the preview is a mirror
  tgn_camera_set_preview(cam, &Watch::On, &preview, 160, 120, 15, 1);

  Side a, b;
  a.peer = &b;
  b.peer = &a;
  uint8_t key[256];
  for (int i = 0; i < 256; ++i) key[i] = (uint8_t)(i * 29 + 3);
  auto make = [&](Side &s, bool outgoing, tgn_camera *camera) {
    tgn_call_params p = {};
    p.version = ver.c_str();
    p.key = key;
    p.is_outgoing = outgoing;
    p.enable_p2p = 1;
    p.max_layer = tgn_max_layer();
    p.audio_mode = TGN_AUDIO_TEST;
    p.test_tone_hz = outgoing ? 440 : 1000;
    p.camera = camera;
    tgn_callbacks cb = {};
    cb.user = &s;
    cb.state = OnState;
    cb.signaling = OnSignal;
    cb.remote_media = OnMedia;
    s.call = tgn_call_create(&p, &cb);
  };
  make(a, true, cam);
  make(b, false, nullptr);
  if (!a.call || !b.call) {
    r += "create failed\n";
    if (a.call) tgn_call_destroy(a.call);
    if (b.call) tgn_call_destroy(b.call);
    tgn_camera_destroy(cam);
    g_h264Encode = h264Before;
    std::snprintf(report, len, "%s", r.c_str());
    return 2;
  }
  tgn_call_set_remote_video(b.call, &Watch::On, &remote, 320, 240);

  std::atomic<bool> run{true};
  std::thread camera([&] {   // 30 pictures a second, like the frame reader
    std::vector<uint8_t> nv12;
    int i = 0;
    auto next = std::chrono::steady_clock::now();
    while (run) {
      Pattern(nv12, 640, 480, i++);
      tgn_frame_in f = {};
      f.format = TGN_PIX_NV12;
      f.width = 640;
      f.height = 480;
      f.plane0 = nv12.data();
      f.stride0 = 640;
      f.plane1 = nv12.data() + 640 * 480;
      f.stride1 = 640;
      tgn_camera_push(cam, &f);
      next += std::chrono::milliseconds(33);
      std::this_thread::sleep_until(next);
    }
  });

  using clock = std::chrono::steady_clock;
  auto pump = [&](std::function<bool()> done, int ms) {
    auto end = clock::now() + std::chrono::milliseconds(ms);
    while (clock::now() < end) {
      Deliver(a);
      Deliver(b);
      if (done()) return true;
      std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    return false;
  };
  auto t0 = clock::now();
  auto since = [&] { return (long long)std::chrono::duration_cast<std::chrono::milliseconds>(clock::now() - t0).count(); };
  char line[512];

  // 1. connect; B sees A's picture the right way round, A's preview is mirrored
  bool seen = pump([&] { return a.state == 2 && b.state == 2 && remote.good >= 20 && preview.good >= 5 && b.remoteVideo == 2; },
                   (seconds > 0 ? seconds : 20) * 1000);
  tgn_video_stats vs = {};
  tgn_call_video_stats(b.call, &vs);
  std::snprintf(line, sizeof(line),
                "1 video: %s after %lld ms - states A %d B %d; B got %d pictures (%d right way round, last %dx%d shown, %dx%d "
                "decoded), remote video %d; A preview %d (%d mirrored)\n",
                seen ? "ok" : "FAILED", since(), a.state.load(), b.state.load(), remote.frames.load(), remote.good.load(),
                remote.w.load(), remote.h.load(), vs.remote_width, vs.remote_height, b.remoteVideo.load(), preview.frames.load(),
                preview.good.load());
  r += line;

  // 2. camera off: B is told its peer's video is inactive
  bool off = false, on = false;
  if (seen) {
    tgn_call_set_camera(a.call, nullptr);
    auto t1 = clock::now();
    off = pump([&] { return b.remoteVideo == 0; }, 8000);
    std::snprintf(line, sizeof(line), "2 camera off: %s (B remote video %d after %lld ms)\n", off ? "ok" : "FAILED",
                  b.remoteVideo.load(),
                  (long long)std::chrono::duration_cast<std::chrono::milliseconds>(clock::now() - t1).count());
    r += line;
  }
  // 3. on again: pictures come back
  if (off) {
    int base = remote.good;
    tgn_call_set_camera(a.call, cam);
    auto t2 = clock::now();
    on = pump([&] { return b.remoteVideo == 2 && remote.good >= base + 10; }, 10000);
    std::snprintf(line, sizeof(line), "3 camera on: %s (B remote video %d, %d more pictures, %lld ms)\n", on ? "ok" : "FAILED",
                  b.remoteVideo.load(), remote.good.load() - base,
                  (long long)std::chrono::duration_cast<std::chrono::milliseconds>(clock::now() - t2).count());
    r += line;
  }

  run = false;
  camera.join();
  tgn_camera_stats cs = {};
  tgn_camera_stats_get(cam, &cs);
  std::snprintf(line, sizeof(line), "camera: %d pushed, %d sent, %d dropped, last %dx%d\n", cs.pushed, cs.sent, cs.dropped, cs.width,
                cs.height);
  r += line;
  int sta = tgn_call_stop(a.call, nullptr, 0, 5000), stb = tgn_call_stop(b.call, nullptr, 0, 5000);
  r += std::string("stop: A ") + (sta ? "TIMED OUT" : "ok") + ", B " + (stb ? "TIMED OUT" : "ok") + "\n";
  tgn_call_destroy(a.call);
  tgn_call_destroy(b.call);
  tgn_camera_destroy(cam);
  char after[128];
  tgn_encoders(after, sizeof(after));
  r += std::string("encoders made: before ") + before + "; after " + after + "\n";
  g_h264Encode = h264Before;
  const bool pass = seen && off && on && !sta && !stb;
  r += pass ? "PASS\n" : "FAIL\n";
  if (report && len > 0) std::snprintf(report, len, "%s", r.c_str());
  return pass ? 0 : 1;
}

}  // extern "C"
