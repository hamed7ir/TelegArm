// tgcalls_native: the Windows 8.1 camera. 8.1 has no MediaFrameReader, but its MediaCapture streams into a custom Media
// Foundation sink (StartPreviewToCustomSinkAsync, Windows 8.0+): this is that sink, wrapped as
// Windows.Media.IMediaExtension so a desktop .NET process can hand it over (Marshal.GetObjectForIUnknown + cast). Each
// sample is pushed into a tgn_camera. Pattern as mmaitre314/MediaReader (Apache-2.0), webrtc-uwp's
// video_capture_sink_winrt (BSD-3) and Microsoft's SimpleCommunication sample (MIT); written for TelegArm.
// Media Foundation is resolved at run time (mfplat.dll is missing on Windows "N" editions): no import, so the DLL loads.
#include <windows.h>
#include <wrl.h>
#include <wrl/implements.h>
#include <windows.media.h>
#include <windows.foundation.collections.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfobjects.h>

#include <atomic>
#include <cstdint>
#include <memory>
#include <string>

#include "tgcalls_internal.h"

using namespace Microsoft::WRL;

namespace {

// ── Media Foundation, resolved at run time ───────────────────────────────────────────────────────────────────────────
struct Mf {
  HRESULT(WINAPI *Startup)(ULONG, DWORD) = nullptr;
  HRESULT(WINAPI *CreateEventQueue)(IMFMediaEventQueue **) = nullptr;
  HRESULT(WINAPI *CreateMediaType)(IMFMediaType **) = nullptr;
  bool ok = false;
};

const Mf &MediaFoundation() {
  static Mf mf = [] {
    Mf m;
    HMODULE h = LoadLibraryExW(L"mfplat.dll", nullptr, 0x800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
    if (!h) h = LoadLibraryW(L"mfplat.dll");
    if (!h) return m;
    m.Startup = (decltype(m.Startup))GetProcAddress(h, "MFStartup");
    m.CreateEventQueue = (decltype(m.CreateEventQueue))GetProcAddress(h, "MFCreateEventQueue");
    m.CreateMediaType = (decltype(m.CreateMediaType))GetProcAddress(h, "MFCreateMediaType");
    m.ok = m.Startup && m.CreateEventQueue && m.CreateMediaType && SUCCEEDED(m.Startup(MF_VERSION, MFSTARTUP_LITE));
    return m;
  }();
  return mf;
}

int PixOf(const GUID &g) {
  if (g == MFVideoFormat_NV12) return TGN_PIX_NV12;
  if (g == MFVideoFormat_YUY2) return TGN_PIX_YUY2;
  if (g == MFVideoFormat_RGB32 || g == MFVideoFormat_ARGB32) return TGN_PIX_BGRA;
  return 0;
}

// ── the stream sink: one video stream, pull-driven (it asks for every next sample) ──────────────────────────────────
class StreamSink final
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, ChainInterfaces<IMFStreamSink, IMFMediaEventGenerator>, IMFMediaTypeHandler> {
 public:
  // sink: NOT owned (the media sink owns this stream; a strong pointer back would be a cycle that only an MF Shutdown
  // breaks, and a failed start may never send one). The media sink clears it in Shutdown and in its destructor.
  StreamSink(IMFMediaSink *sink, std::shared_ptr<tgn::CameraCore> cam, UINT32 w, UINT32 h, UINT32 fps)
      : sink_(sink), cam_(std::move(cam)), reqW_(w), reqH_(h) {
    const Mf &mf = MediaFoundation();
    mf.CreateEventQueue(&queue_);
    mf.CreateMediaType(&preferred_);
    preferred_->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    preferred_->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    MFSetAttributeSize(preferred_.Get(), MF_MT_FRAME_SIZE, w, h);
    MFSetAttributeRatio(preferred_.Get(), MF_MT_FRAME_RATE, fps, 1);
    preferred_->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    preferred_->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
  }

  void Started() {
    started_ = true;
    Queue(MEStreamSinkStarted);
    RequestSample();
  }
  void Stopped() {
    started_ = false;
    Queue(MEStreamSinkStopped);
  }
  void Paused() { Queue(MEStreamSinkPaused); }
  void Shutdown() {
    ComPtr<IMFMediaEventQueue> q;
    std::shared_ptr<tgn::CameraCore> cam;
    {
      auto lock = cs_.Lock();   // ProcessSample copies cam_ under this lock: no sample uses it after this
      shutdown_ = true;
      q.Swap(queue_);
      cam.swap(cam_);
      sink_ = nullptr;
    }
    if (q) q->Shutdown();
    current_.Reset();
    cam.reset();
  }

  // IMFStreamSink
  IFACEMETHODIMP GetMediaSink(IMFMediaSink **p) override {
    if (!p) return E_POINTER;
    *p = nullptr;
    auto lock = cs_.Lock();
    if (shutdown_ || !sink_) return MF_E_SHUTDOWN;
    sink_->AddRef();
    *p = sink_;
    return S_OK;
  }
  IFACEMETHODIMP GetIdentifier(DWORD *id) override {
    if (!id) return E_POINTER;
    *id = 0;
    return S_OK;
  }
  IFACEMETHODIMP GetMediaTypeHandler(IMFMediaTypeHandler **p) override {
    if (!p) return E_POINTER;
    *p = this;
    AddRef();
    return S_OK;
  }
  // Copies (converts) the picture into the camera before it returns, then asks for the next one at once: a sink that
  // does not re-request gets nothing more, and one that blocks slows the camera down (both measured on the dev box).
  IFACEMETHODIMP ProcessSample(IMFSample *s) override {
    std::shared_ptr<tgn::CameraCore> cam;
    {
      auto lock = cs_.Lock();   // a Shutdown on another MF thread may clear cam_ meanwhile
      if (shutdown_) return MF_E_SHUTDOWN;
      cam = cam_;
    }
    if (s && cam && pix_) {
      ComPtr<IMFMediaBuffer> buf;
      if (SUCCEEDED(s->ConvertToContiguousBuffer(&buf))) {
        BYTE *p = nullptr;
        LONG pitch = 0;
        DWORD len = 0;
        ComPtr<IMF2DBuffer> b2;
        if (SUCCEEDED(buf.As(&b2)) && SUCCEEDED(b2->Lock2D(&p, &pitch))) {
          PushPicture(*cam, p, pitch);
          b2->Unlock2D();
        } else if (SUCCEEDED(buf->Lock(&p, nullptr, &len))) {
          // a plain buffer starts with the first row in memory: the bottom one when the type is bottom-up
          const BYTE *top = defaultStride_ < 0 ? p + (ptrdiff_t)(height_ - 1) * -defaultStride_ : p;
          PushPicture(*cam, top, defaultStride_);
          buf->Unlock();
        }
      }
    }
    if (started_) RequestSample();
    return S_OK;
  }
  IFACEMETHODIMP PlaceMarker(MFSTREAMSINK_MARKER_TYPE, const PROPVARIANT *, const PROPVARIANT *ctx) override {
    return Queue(MEStreamSinkMarker, ctx);
  }
  IFACEMETHODIMP Flush() override { return S_OK; }

  // IMFMediaEventGenerator
  IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent **ev) override {
    auto q = Q();
    return q ? q->GetEvent(flags, ev) : MF_E_SHUTDOWN;
  }
  IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback *cb, IUnknown *st) override {
    auto q = Q();
    return q ? q->BeginGetEvent(cb, st) : MF_E_SHUTDOWN;
  }
  IFACEMETHODIMP EndGetEvent(IMFAsyncResult *r, IMFMediaEvent **ev) override {
    auto q = Q();
    return q ? q->EndGetEvent(r, ev) : MF_E_SHUTDOWN;
  }
  IFACEMETHODIMP QueueEvent(MediaEventType met, REFGUID ext, HRESULT hr, const PROPVARIANT *v) override {
    auto q = Q();
    return q ? q->QueueEventParamVar(met, ext, hr, v) : MF_E_SHUTDOWN;
  }

  // IMFMediaTypeHandler. The camera's own type is offered first: accepting it avoids a conversion, so we take any
  // NV12 / YUY2 / RGB32 no larger than asked; a larger one is refused and MediaCapture converts to the profile's type.
  IFACEMETHODIMP IsMediaTypeSupported(IMFMediaType *mt, IMFMediaType **closest) override {
    if (closest) *closest = nullptr;
    if (!mt) return E_POINTER;
    GUID major = GUID_NULL, sub = GUID_NULL;
    mt->GetGUID(MF_MT_MAJOR_TYPE, &major);
    mt->GetGUID(MF_MT_SUBTYPE, &sub);
    UINT32 w = 0, h = 0;
    MFGetAttributeSize(mt, MF_MT_FRAME_SIZE, &w, &h);
    const bool ok = major == MFMediaType_Video && PixOf(sub) && w >= 2 && h >= 2 && w <= reqW_ && h <= reqH_;
    return ok ? S_OK : MF_E_INVALIDMEDIATYPE;
  }
  IFACEMETHODIMP GetMediaTypeCount(DWORD *n) override {
    if (!n) return E_POINTER;
    *n = 1;
    return S_OK;
  }
  IFACEMETHODIMP GetMediaTypeByIndex(DWORD i, IMFMediaType **mt) override {
    if (!mt) return E_POINTER;
    if (i != 0) return MF_E_NO_MORE_TYPES;
    return preferred_.CopyTo(mt);
  }
  IFACEMETHODIMP SetCurrentMediaType(IMFMediaType *mt) override {
    if (!mt) return E_POINTER;
    HRESULT hr = IsMediaTypeSupported(mt, nullptr);
    if (FAILED(hr)) return hr;
    ComPtr<IMFMediaType> copy;
    MediaFoundation().CreateMediaType(&copy);
    mt->CopyAllItems(copy.Get());
    current_ = copy;
    GUID sub = GUID_NULL;
    mt->GetGUID(MF_MT_SUBTYPE, &sub);
    UINT32 w = 0, h = 0;
    MFGetAttributeSize(mt, MF_MT_FRAME_SIZE, &w, &h);
    width_ = (int)w;
    height_ = (int)h;
    pix_ = PixOf(sub);
    const int natural = pix_ == TGN_PIX_NV12 ? width_ : pix_ == TGN_PIX_YUY2 ? width_ * 2 : width_ * 4;
    UINT32 stride = 0;
    // MF_MT_DEFAULT_STRIDE is a signed value stored as UINT32: negative = bottom-up. Without it, RGB is bottom-up.
    if (SUCCEEDED(mt->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride))) defaultStride_ = (int)(INT32)stride;
    else defaultStride_ = pix_ == TGN_PIX_BGRA ? -natural : natural;
    tgn::Log("tgn: camera sink " + std::to_string(width_) + "x" + std::to_string(height_) + " format " + std::to_string(pix_) +
             " stride " + std::to_string(defaultStride_));
    return S_OK;
  }
  IFACEMETHODIMP GetCurrentMediaType(IMFMediaType **mt) override {
    if (!mt) return E_POINTER;
    if (!current_) return MF_E_NOT_INITIALIZED;
    return current_.CopyTo(mt);
  }
  IFACEMETHODIMP GetMajorType(GUID *g) override {
    if (!g) return E_POINTER;
    *g = MFMediaType_Video;
    return S_OK;
  }

 private:
  ComPtr<IMFMediaEventQueue> Q() {
    auto lock = cs_.Lock();
    return queue_;
  }
  HRESULT Queue(MediaEventType type, const PROPVARIANT *v = nullptr) {
    auto q = Q();
    return q ? q->QueueEventParamVar(type, GUID_NULL, S_OK, v) : MF_E_SHUTDOWN;
  }
  void RequestSample() { Queue(MEStreamSinkRequestSample); }

  // A negative pitch means bottom-up: p is still the top row, so stepping by the negative pitch walks down the picture.
  void PushPicture(tgn::CameraCore &cam, const BYTE *p, LONG pitch) {
    tgn_frame_in f = {};
    f.format = pix_;
    f.width = width_;
    f.height = height_;
    f.plane0 = p;
    f.stride0 = (int32_t)pitch;
    if (pix_ == TGN_PIX_NV12) {   // the interleaved UV plane follows the Y plane at the same pitch
      f.plane1 = p + (ptrdiff_t)pitch * height_;
      f.stride1 = (int32_t)pitch;
    }
    tgn::Push(cam, f);
  }

  IMFMediaSink *sink_;   // not owned (see the constructor); guarded by cs_
  std::shared_ptr<tgn::CameraCore> cam_;   // guarded by cs_
  Wrappers::CriticalSection cs_;
  ComPtr<IMFMediaEventQueue> queue_;
  ComPtr<IMFMediaType> current_, preferred_;
  UINT32 reqW_, reqH_;
  int width_ = 0, height_ = 0, pix_ = 0, defaultStride_ = 0;
  std::atomic<bool> shutdown_{false}, started_{false};
};

// ── the media sink: fixed, one stream, rateless; started and stopped by the presentation clock ─────────────────────
class MediaSink final : public RuntimeClass<RuntimeClassFlags<WinRtClassicComMix>, ABI::Windows::Media::IMediaExtension,
                                            CloakedIid<IMFMediaSink>, CloakedIid<IMFClockStateSink>, FtmBase> {
  InspectableClass(L"TelegArm.CameraSink", BaseTrust)

 public:
  ~MediaSink() {
    if (stream_) stream_->Shutdown();   // never started (or MF never shut us down): the stream lets go of the camera
  }

  HRESULT RuntimeClassInitialize(std::shared_ptr<tgn::CameraCore> cam, UINT32 w, UINT32 h, UINT32 fps) {
    stream_ = Make<StreamSink>(static_cast<IMFMediaSink *>(this), std::move(cam), w, h, fps);
    return stream_ ? S_OK : E_OUTOFMEMORY;
  }

  // IMediaExtension
  IFACEMETHODIMP SetProperties(ABI::Windows::Foundation::Collections::IPropertySet *) override { return S_OK; }

  // IMFMediaSink
  IFACEMETHODIMP GetCharacteristics(DWORD *c) override {
    if (!c) return E_POINTER;
    *c = MEDIASINK_RATELESS | MEDIASINK_FIXED_STREAMS;
    return S_OK;
  }
  IFACEMETHODIMP AddStreamSink(DWORD, IMFMediaType *, IMFStreamSink **s) override {
    if (s) *s = nullptr;
    return MF_E_STREAMSINKS_FIXED;
  }
  IFACEMETHODIMP RemoveStreamSink(DWORD) override { return MF_E_STREAMSINKS_FIXED; }
  IFACEMETHODIMP GetStreamSinkCount(DWORD *n) override {
    if (!n) return E_POINTER;
    *n = shutdown_ ? 0 : 1;
    return shutdown_ ? MF_E_SHUTDOWN : S_OK;
  }
  IFACEMETHODIMP GetStreamSinkByIndex(DWORD i, IMFStreamSink **s) override {
    if (!s) return E_POINTER;
    *s = nullptr;
    if (shutdown_) return MF_E_SHUTDOWN;
    if (i != 0) return MF_E_INVALIDINDEX;
    return stream_.CopyTo(s);
  }
  IFACEMETHODIMP GetStreamSinkById(DWORD id, IMFStreamSink **s) override {
    if (!s) return E_POINTER;
    *s = nullptr;
    if (shutdown_) return MF_E_SHUTDOWN;
    if (id != 0) return MF_E_INVALIDSTREAMNUMBER;
    return stream_.CopyTo(s);
  }
  IFACEMETHODIMP SetPresentationClock(IMFPresentationClock *clock) override {
    if (shutdown_) return MF_E_SHUTDOWN;
    if (clock_) {
      clock_->RemoveClockStateSink(static_cast<IMFClockStateSink *>(this));
      clock_.Reset();
    }
    if (clock) {
      HRESULT hr = clock->AddClockStateSink(static_cast<IMFClockStateSink *>(this));
      if (FAILED(hr)) return hr;
      clock_ = clock;
    }
    return S_OK;
  }
  IFACEMETHODIMP GetPresentationClock(IMFPresentationClock **c) override {
    if (!c) return E_POINTER;
    *c = nullptr;
    if (!clock_) return MF_E_NO_CLOCK;
    return clock_.CopyTo(c);
  }
  IFACEMETHODIMP Shutdown() override {
    if (shutdown_.exchange(true)) return S_OK;
    if (clock_) {
      clock_->RemoveClockStateSink(static_cast<IMFClockStateSink *>(this));
      clock_.Reset();
    }
    if (stream_) {
      stream_->Shutdown();
      stream_.Reset();
    }
    return S_OK;
  }

  // IMFClockStateSink
  IFACEMETHODIMP OnClockStart(MFTIME, LONGLONG) override {
    if (stream_) stream_->Started();
    return S_OK;
  }
  IFACEMETHODIMP OnClockStop(MFTIME) override {
    if (stream_) stream_->Stopped();
    return S_OK;
  }
  IFACEMETHODIMP OnClockPause(MFTIME) override {
    if (stream_) stream_->Paused();
    return S_OK;
  }
  IFACEMETHODIMP OnClockRestart(MFTIME) override {
    if (stream_) stream_->Started();
    return S_OK;
  }
  IFACEMETHODIMP OnClockSetRate(MFTIME, float) override { return S_OK; }

 private:
  ComPtr<StreamSink> stream_;
  ComPtr<IMFPresentationClock> clock_;
  std::atomic<bool> shutdown_{false};
};

}  // namespace

extern "C" {

TGN_API void *TGN_CALL tgn_camsink_create(tgn_camera *cam, int32_t width, int32_t height, int32_t fps) {
  auto core = tgn::CoreOf(cam);
  if (!core) return nullptr;
  if (!MediaFoundation().ok) {
    tgn::Log("tgn: camera sink: Media Foundation is not available");
    return nullptr;
  }
  ComPtr<MediaSink> sink;
  HRESULT hr = MakeAndInitialize<MediaSink>(&sink, core, (UINT32)(width > 0 ? width : 640), (UINT32)(height > 0 ? height : 480),
                                            (UINT32)(fps > 0 ? fps : 30));
  if (FAILED(hr)) return nullptr;
  ComPtr<IInspectable> insp;
  if (FAILED(sink.As(&insp))) return nullptr;
  return insp.Detach();
}

}  // extern "C"
