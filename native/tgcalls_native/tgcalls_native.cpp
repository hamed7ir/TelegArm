// tgcalls_native: TelegArm's C ABI over tgcalls (see tgcalls_native.h). Compiled with the defines in the tgcalls
// package's BUILD-INFO.txt (they are ABI-relevant), /std:c++20 /EHsc /MT, RTTI on; linked with rt2.1's fixed lld-link.
#include <winsock2.h>
#include <windows.h>
#include <mmsystem.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "tgcalls/Instance.h"
#include "tgcalls/FakeAudioDeviceModule.h"
#include "tgcalls/StaticThreads.h"
#include "rtc_base/thread.h"

#include "tgcalls_native.h"
#include "tgcalls_internal.h"
#include "tgcalls_audio.h"

// As tdesktop (calls_call.cpp): the implementation headers are not public; declare the classes for Register<>.
namespace tgcalls {
class InstanceImpl;
class InstanceV2Impl;
class InstanceV2ReferenceImpl;
}  // namespace tgcalls

namespace {

constexpr int kVersionMajor = 1, kVersionMinor = 2;   // 1.1: video; 1.2: group calls
constexpr int kRate = 48000;           // the call's audio: 48 kHz mono, 10 ms frames
constexpr int kChannels = 1;
constexpr int kFrame = kRate / 100;
constexpr int kPlayReserveMs = 40;   // what the renderer keeps queued ahead of the speaker, besides WinMM's 60 ms

// ── logging ──────────────────────────────────────────────────────────────────────────────────────────────────────────
std::mutex g_logMutex;
tgn_log_fn g_log = nullptr;
void *g_logUser = nullptr;

void Log(const std::string &line) {
  std::lock_guard<std::mutex> lock(g_logMutex);
  if (g_log) g_log(g_logUser, line.c_str());
}

// ── a sample ring ────────────────────────────────────────────────────────────────────────────────────────────────────
class Ring {
 public:
  explicit Ring(size_t capacity) : buf_(capacity) {}
  // Adds n samples; when full, the OLDEST are dropped (latency stays bounded). Returns how many were dropped.
  size_t Push(const int16_t *s, size_t n) {
    std::lock_guard<std::mutex> lock(m_);
    size_t dropped = 0;
    for (size_t i = 0; i < n; ++i) {
      if (count_ == buf_.size()) { r_ = (r_ + 1) % buf_.size(); --count_; ++dropped; }
      buf_[w_] = s[i];
      w_ = (w_ + 1) % buf_.size();
      ++count_;
    }
    return dropped;
  }
  size_t Pop(int16_t *out, size_t n) {
    std::lock_guard<std::mutex> lock(m_);
    size_t k = std::min(n, count_);
    for (size_t i = 0; i < k; ++i) { out[i] = buf_[r_]; r_ = (r_ + 1) % buf_.size(); }
    count_ -= k;
    return k;
  }
  size_t Size() {
    std::lock_guard<std::mutex> lock(m_);
    return count_;
  }
  void Clear() {
    std::lock_guard<std::mutex> lock(m_);
    r_ = w_ = count_ = 0;
  }

 private:
  std::mutex m_;
  std::vector<int16_t> buf_;
  size_t r_ = 0, w_ = 0, count_ = 0;
};

// ── audio threads: priority and a fine timer ─────────────────────────────────────────────────────────────────────────
// RT trip 2026-10-16: the other side's voice came out choppy and late on the Surface (video on). Two causes:
//  * tgcalls' FakeAudioDeviceModule paces its loops with std::this_thread::sleep_for, which Windows rounds up to the
//    system timer tick - 15.6 ms unless some program raised it (an idle RT has not): the renderer's 2-10 ms waits became
//    16 ms and the speaker ran dry. So its loops run on OUR threads (Options.scheduler_) with a 1 ms timer
//    (timeBeginPeriod, held while they run);
//  * VP8 encoding and decoding load the Tegra: the audio threads get audio priority (MMCSS "Pro Audio" when avrt.dll is
//    there, else TIME_CRITICAL), as every voice app's do.
class AudioPriority {
 public:
  AudioPriority() {
    static auto set = (HANDLE(WINAPI *)(LPCWSTR, LPDWORD))GetProcAddress(LoadLibraryW(L"avrt.dll"), "AvSetMmThreadCharacteristicsW");
    DWORD index = 0;
    if (set) h_ = set(L"Pro Audio", &index);
    if (!h_) SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
  }
  ~AudioPriority() {
    static auto revert = (BOOL(WINAPI *)(HANDLE))GetProcAddress(LoadLibraryW(L"avrt.dll"), "AvRevertMmThreadCharacteristics");
    if (h_ && revert) revert(h_);
  }

 private:
  HANDLE h_ = nullptr;
};

class FineTimer {   // the 1 ms system timer while this lives (reference-counted by Windows)
 public:
  FineTimer() { timeBeginPeriod(1); }
  ~FineTimer() { timeEndPeriod(1); }
};

void AudioScheduler(tgcalls::FakeAudioDeviceModule::Task task) {
  std::thread([task = std::move(task)]() mutable {
    AudioPriority priority;
    FineTimer timer;
    while (true) {
      double wait = task();   // seconds; negative = stop
      if (wait < 0) return;
      if (wait > 0) std::this_thread::sleep_for(std::chrono::microseconds((int64_t)(wait * 1000000.0)));
    }
  }).detach();
}

WAVEFORMATEX Format() {
  WAVEFORMATEX f = {};
  f.wFormatTag = WAVE_FORMAT_PCM;
  f.nChannels = kChannels;
  f.nSamplesPerSec = kRate;
  f.wBitsPerSample = 16;
  f.nBlockAlign = (WORD)(kChannels * 2);
  f.nAvgBytesPerSec = kRate * f.nBlockAlign;
  return f;
}

// ── WinMM capture: 4 x 20 ms buffers, refilled on an event thread, into a ring ───────────────────────────────────────
class WinmmCapture {
 public:
  explicit WinmmCapture(Ring &ring, std::atomic<int> &overruns) : ring_(ring), overruns_(overruns) {}
  ~WinmmCapture() { Stop(); }
  bool Start() {
    if (h_) return true;
    ev_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    WAVEFORMATEX f = Format();
    MMRESULT r = waveInOpen(&h_, WAVE_MAPPER, &f, (DWORD_PTR)ev_, 0, CALLBACK_EVENT);
    if (r != MMSYSERR_NOERROR) { Log("tgn: waveInOpen failed " + std::to_string(r)); h_ = nullptr; CloseHandle(ev_); ev_ = nullptr; return false; }
    for (int i = 0; i < kBuffers; ++i) {
      bufs_[i].assign(kFrame * 2 * kChannels, 0);
      std::memset(&hdr_[i], 0, sizeof(WAVEHDR));
      hdr_[i].lpData = (LPSTR)bufs_[i].data();
      hdr_[i].dwBufferLength = (DWORD)(bufs_[i].size() * 2);
      waveInPrepareHeader(h_, &hdr_[i], sizeof(WAVEHDR));
      waveInAddBuffer(h_, &hdr_[i], sizeof(WAVEHDR));
    }
    stop_ = false;
    thread_ = std::thread([this] { Loop(); });
    waveInStart(h_);
    return true;
  }
  void Stop() {
    if (!h_) return;
    stop_ = true;
    SetEvent(ev_);
    if (thread_.joinable()) thread_.join();
    waveInReset(h_);
    for (int i = 0; i < kBuffers; ++i) waveInUnprepareHeader(h_, &hdr_[i], sizeof(WAVEHDR));
    waveInClose(h_);
    h_ = nullptr;
    CloseHandle(ev_);
    ev_ = nullptr;
  }

 private:
  static constexpr int kBuffers = 4;
  void Loop() {
    AudioPriority priority;
    while (!stop_) {
      WaitForSingleObject(ev_, 100);
      for (int i = 0; i < kBuffers && !stop_; ++i) {
        if (!(hdr_[i].dwFlags & WHDR_DONE)) continue;
        size_t n = hdr_[i].dwBytesRecorded / 2;
        if (n && ring_.Push((const int16_t *)hdr_[i].lpData, n)) overruns_++;
        hdr_[i].dwFlags &= ~WHDR_DONE;
        waveInAddBuffer(h_, &hdr_[i], sizeof(WAVEHDR));
      }
    }
  }
  Ring &ring_;
  std::atomic<int> &overruns_;
  HWAVEIN h_ = nullptr;
  HANDLE ev_ = nullptr;
  WAVEHDR hdr_[kBuffers] = {};
  std::vector<int16_t> bufs_[kBuffers];
  std::thread thread_;
  std::atomic<bool> stop_{false};
};

// ── WinMM playback: 3 x 20 ms buffers fed from a ring (silence when it runs dry) ─────────────────────────────────────
// The device starts once the ring holds a reserve (no burst of silence first); a buffer coming back finds the ring above
// one buffer because the renderer keeps it at kPlayReserveMs (RenderWaitUs), independent of what WinMM holds. (It was
// "60 ms ahead INCLUDING WinMM's 80 ms": WinMM alone was above that, so the ring stayed short and every buffer was part
// silence - the choppy voice of the RT trip.)
class WinmmPlayback {
 public:
  WinmmPlayback(Ring &ring, std::atomic<int> &underruns) : ring_(ring), underruns_(underruns) {}
  ~WinmmPlayback() { Stop(); }
  bool Start() {
    if (h_) return true;
    ev_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    WAVEFORMATEX f = Format();
    MMRESULT r = waveOutOpen(&h_, WAVE_MAPPER, &f, (DWORD_PTR)ev_, 0, CALLBACK_EVENT);
    if (r != MMSYSERR_NOERROR) { Log("tgn: waveOutOpen failed " + std::to_string(r)); h_ = nullptr; CloseHandle(ev_); ev_ = nullptr; return false; }
    for (int i = 0; i < kBuffers; ++i) {
      bufs_[i].assign(kFrame * 2 * kChannels, 0);
      std::memset(&hdr_[i], 0, sizeof(WAVEHDR));
      hdr_[i].lpData = (LPSTR)bufs_[i].data();
      hdr_[i].dwBufferLength = (DWORD)(bufs_[i].size() * 2);
      waveOutPrepareHeader(h_, &hdr_[i], sizeof(WAVEHDR));
      hdr_[i].dwFlags |= WHDR_DONE;   // free: the loop fills it
    }
    stop_ = false;
    thread_ = std::thread([this] { Loop(); });
    SetEvent(ev_);
    return true;
  }
  void Stop() {
    if (!h_) return;
    stop_ = true;
    SetEvent(ev_);
    if (thread_.joinable()) thread_.join();
    waveOutReset(h_);
    for (int i = 0; i < kBuffers; ++i) waveOutUnprepareHeader(h_, &hdr_[i], sizeof(WAVEHDR));
    waveOutClose(h_);
    h_ = nullptr;
    CloseHandle(ev_);
    ev_ = nullptr;
  }
  // Samples written to the device and not yet played.
  size_t Queued() const { return queued_.load(); }

 private:
  static constexpr int kBuffers = 3;
  void Loop() {
    AudioPriority priority;
    FineTimer timer;
    const size_t reserve = (size_t)kRate * kPlayReserveMs / 1000;
    while (!stop_ && !started_) {   // the first buffers go out together, once a reserve has built up
      if (ring_.Size() >= reserve) { started_ = true; break; }
      Sleep(2);
    }
    while (!stop_) {
      WaitForSingleObject(ev_, 50);
      for (int i = 0; i < kBuffers && !stop_; ++i) {
        if (!(hdr_[i].dwFlags & WHDR_DONE)) continue;
        if (hdr_[i].dwUser) queued_ -= (size_t)hdr_[i].dwUser;
        size_t want = bufs_[i].size();
        for (int grace = 0; grace < 3 && ring_.Size() < want; ++grace) Sleep(1);   // the renderer may be a frame late
        size_t got = ring_.Pop(bufs_[i].data(), want);
        if (got < want) {
          std::fill(bufs_[i].begin() + got, bufs_[i].end(), (int16_t)0);
          underruns_++;
        }
        hdr_[i].dwFlags &= ~WHDR_DONE;
        hdr_[i].dwUser = want;
        queued_ += want;
        waveOutWrite(h_, &hdr_[i], sizeof(WAVEHDR));
      }
    }
  }
  Ring &ring_;
  std::atomic<int> &underruns_;
  HWAVEOUT h_ = nullptr;
  HANDLE ev_ = nullptr;
  WAVEHDR hdr_[kBuffers] = {};
  std::vector<int16_t> bufs_[kBuffers];
  std::thread thread_;
  std::atomic<bool> stop_{false};
  std::atomic<size_t> queued_{0};
  std::atomic<bool> started_{false};
};

// ── the call's audio: one object is both the recorder and the renderer FakeAudioDeviceModule drives ─────────────────
class CallAudio : public std::enable_shared_from_this<CallAudio> {
 public:
  CallAudio(int mode, int toneHz, bool micAllowed = true)
      : mode_(mode), toneHz_(toneHz), capRing_(kRate / 2), playRing_(kRate / 2), frame_(kFrame * kChannels),
        capture_(capRing_, overruns_), playback_(playRing_, underruns_), micAllowed_(micAllowed) {}

  // A group call is joined muted and tgcalls polls the recorder from the start: until the first unmute the microphone
  // stays closed (no Windows "in use" mark, no WinMM capture) - the recorder just says "nothing yet".
  void SetMicAllowed(bool allowed) { micAllowed_ = allowed; }
  ~CallAudio() {
    capture_.Stop();
    playback_.Stop();
  }

  // Recorder: device mode returns 10 ms once WinMM has it (num_samples 0 = "not yet", polled every 2 ms), so the
  // microphone's own clock paces the call; test mode makes a tone on a 10 ms deadline.
  tgcalls::AudioFrame Record() {
    tgcalls::AudioFrame f = {};
    f.bytes_per_sample = 2 * kChannels;
    f.num_channels = kChannels;
    f.samples_per_sec = kRate;
    f.elapsed_time_ms = -1;
    f.ntp_time_ms = -1;
    if (mode_ == TGN_AUDIO_TEST || mode_ == TGN_AUDIO_SPEAKER_TEST) {
      for (int i = 0; i < kFrame; ++i)
        frame_[i] = (int16_t)(8000.0 * std::sin(2.0 * 3.141592653589793 * toneHz_ * (double)(phase_++) / kRate));
      f.audio_samples = frame_.data();
      f.num_samples = kFrame;
      recorded_++;
      return f;
    }
    if (!micAllowed_) { f.num_samples = 0; return f; }
    if (!capStarted_) {   // review 2026-10-16: a failed open (device busy / missing) is retried once a second, not given up for the call
      auto now = std::chrono::steady_clock::now();
      if (now >= capRetry_) { capStarted_ = capture_.Start(); if (!capStarted_) capRetry_ = now + std::chrono::seconds(1); }
    }
    if (capRing_.Size() < frame_.size()) { f.num_samples = 0; return f; }
    capRing_.Pop(frame_.data(), frame_.size());
    f.audio_samples = frame_.data();
    f.num_samples = kFrame;
    recorded_++;
    return f;
  }
  int32_t RecordWaitUs() {
    if (mode_ == TGN_AUDIO_TEST || mode_ == TGN_AUDIO_SPEAKER_TEST) return Deadline(recNext_);
    if (!micAllowed_) return 10000;
    return capRing_.Size() >= frame_.size() ? 0 : 2000;
  }

  // Renderer: the 10 ms WebRTC mixed for playout. Device mode queues it for WinMM and keeps ~60 ms ahead of the device
  // (so the speaker's clock paces it); test mode analyses it on a 10 ms deadline.
  bool Render(const tgcalls::AudioFrame &f) {
    rendered_++;
    const int16_t *s = f.audio_samples;
    size_t n = f.num_samples * f.num_channels;
    double sum = 0;
    int crossings = 0;
    for (size_t i = 0; i < n; i += f.num_channels) {
      int16_t v = s[i];
      sum += (double)v * v;
      if ((lastSample_ < 0) != (v < 0)) crossings++;
      lastSample_ = v;
    }
    double rms = n ? std::sqrt(sum / (double)(n / f.num_channels)) : 0;
    if (rms > 300) {
      audible_++;
      crossingsTotal_ += crossings;
      samplesAudible_ += (int64_t)f.num_samples;
      rmsLast_ = (int)rms;
    }
    if (mode_ == TGN_AUDIO_TEST) return true;
    if (!playStarted_) {
      auto now = std::chrono::steady_clock::now();
      if (now >= playRetry_) { playStarted_ = playback_.Start(); if (!playStarted_) playRetry_ = now + std::chrono::seconds(1); }
    }
    std::vector<int16_t> &mono = mono_;
    mono.resize(f.num_samples);
    for (size_t i = 0; i < f.num_samples; ++i) {
      int v = s[i * f.num_channels];
      if (mode_ == TGN_AUDIO_SPEAKER_TEST) v /= 10;   // the speaker self-test: quietly
      mono[i] = (int16_t)v;
    }
    playRing_.Push(mono.data(), mono.size());
    return true;
  }
  // The ring alone sets the pace: below the reserve, at once; above it, a wait for the excess (at most 10 ms), so the
  // speaker's own clock pulls the call's audio.
  int32_t RenderWaitUs() {
    if (mode_ == TGN_AUDIO_TEST) return Deadline(renNext_);
    const size_t ring = playRing_.Size(), reserve = (size_t)kRate * kPlayReserveMs / 1000;
    if (ring < reserve) return 0;
    return (int32_t)std::min<int64_t>(10000, (int64_t)(ring - reserve) * 1000000LL / kRate + 1000);
  }

  void Stats(tgn_audio_stats *o) {
    o->frames_recorded = recorded_;
    o->frames_rendered = rendered_;
    o->frames_audible = audible_;
    o->rendered_hz = samplesAudible_ > 0 ? (int32_t)((double)crossingsTotal_ * kRate / (2.0 * (double)samplesAudible_)) : 0;
    o->rendered_rms = rmsLast_;
    o->capture_overruns = overruns_;
    o->playback_underruns = underruns_;
  }

 private:
  // A steady 10 ms cadence: the wait until the next deadline (no drift from the time each frame took).
  static int32_t Deadline(std::chrono::steady_clock::time_point &next) {
    auto now = std::chrono::steady_clock::now();
    if (next.time_since_epoch().count() == 0) next = now;
    next += std::chrono::milliseconds(10);
    auto us = std::chrono::duration_cast<std::chrono::microseconds>(next - now).count();
    if (us < 0) { next = now; us = 0; }
    return (int32_t)us;
  }

  const int mode_, toneHz_;
  Ring capRing_, playRing_;
  std::vector<int16_t> frame_;
  std::atomic<int> overruns_{0}, underruns_{0};
  WinmmCapture capture_;
  WinmmPlayback playback_;
  bool capStarted_ = false, playStarted_ = false;   // OPENED (a failed open retries after capRetry_ / playRetry_)
  std::chrono::steady_clock::time_point capRetry_{}, playRetry_{};
  int64_t phase_ = 0;
  std::chrono::steady_clock::time_point recNext_{}, renNext_{};
  std::atomic<int> recorded_{0}, rendered_{0}, audible_{0};
  int16_t lastSample_ = 0;
  int64_t crossingsTotal_ = 0, samplesAudible_ = 0;
  int rmsLast_ = 0;
  std::vector<int16_t> mono_;
  std::atomic<bool> micAllowed_;
};

class RecorderAdapter final : public tgcalls::FakeAudioDeviceModule::Recorder {
 public:
  explicit RecorderAdapter(std::shared_ptr<CallAudio> a) : a_(std::move(a)) {}
  tgcalls::AudioFrame Record() override { return a_->Record(); }
  int32_t WaitForUs() override { return a_->RecordWaitUs(); }

 private:
  std::shared_ptr<CallAudio> a_;
};

class RendererAdapter final : public tgcalls::FakeAudioDeviceModule::Renderer {
 public:
  explicit RendererAdapter(std::shared_ptr<CallAudio> a) : a_(std::move(a)) {}
  bool Render(const tgcalls::AudioFrame &samples) override { return a_->Render(samples); }
  int32_t WaitForUs() override { return a_->RenderWaitUs(); }

 private:
  std::shared_ptr<CallAudio> a_;
};

std::array<int, 3> Numeric(const std::string &v) {
  int a = 0, b = 0, c = 0;
  std::sscanf(v.c_str(), "%d.%d.%d", &a, &b, &c);
  return {a, b, c};
}

// Once per process, whoever asks first: Winsock (WebRTC never starts it) and the implementations, as tdesktop registers
// them. Separate from tgn_init's logger, so asking for the versions first does not leave them empty.
void EnsureInit() {
  static std::once_flag once;
  std::call_once(once, [] {
    WSADATA wsa;
    int rc = WSAStartup(MAKEWORD(2, 2), &wsa);   // ref-counted: harmless if .NET already did it
    if (rc) Log("tgn: WSAStartup failed " + std::to_string(rc));
    tgcalls::Register<tgcalls::InstanceImpl>();
    tgcalls::Register<tgcalls::InstanceV2Impl>();
    tgcalls::Register<tgcalls::InstanceV2ReferenceImpl>();
    tgcalls::SetLoggingFunction([](std::string const &s) { Log(s); });
  });
}

void CopyOut(const std::string &s, char *buf, int len) {
  if (!buf || len <= 0) return;
  size_t n = std::min((size_t)(len - 1), s.size());
  std::memcpy(buf, s.data(), n);
  buf[n] = 0;
}

}  // namespace

namespace tgn {
void Log(const std::string &line) { ::Log(line); }
void EnsureInit() { ::EnsureInit(); }

// tgcalls tears an instance (and a video capture) down by POSTING tasks to its threads (ThreadLocalObject). Left queued
// until the process exits, they run inside the static threads' destructors after ExitProcess has killed the threads they
// BlockingCall into, and the process never ends (measured: V1 calls, 2.7.7 / 5.0.0, hung 3 exits of 4). So finish them
// now, while every thread is alive: an empty blocking call on each thread waits for everything posted before it.
void Drain(rtc::Thread *own) {
  auto threads = tgcalls::StaticThreads::getThreads();
  for (int round = 0; round < 2; ++round) {   // a teardown may post on to another thread: twice round covers that
    if (own) own->BlockingCall([] {});
    threads->getMediaThread()->BlockingCall([] {});
    threads->getNetworkThread()->BlockingCall([] {});
    threads->getWorkerThread()->BlockingCall([] {});
  }
}

std::shared_ptr<void> MakeCallAudio(int mode, int toneHz, bool micAllowed) {
  return std::make_shared<CallAudio>(mode, toneHz > 0 ? toneHz : 440, micAllowed);
}

std::function<webrtc::scoped_refptr<webrtc::AudioDeviceModule>(webrtc::TaskQueueFactory *)> AudioDeviceCreator(
    const std::shared_ptr<void> &audio) {
  auto a = std::static_pointer_cast<CallAudio>(audio);
  tgcalls::FakeAudioDeviceModule::Options options;
  options.samples_per_sec = kRate;
  options.num_channels = kChannels;
  options.scheduler_ = AudioScheduler;
  return tgcalls::FakeAudioDeviceModule::Creator(std::make_shared<RendererAdapter>(a), std::make_shared<RecorderAdapter>(a), options);
}

void SetMicAllowed(const std::shared_ptr<void> &audio, bool allowed) {
  if (audio) std::static_pointer_cast<CallAudio>(audio)->SetMicAllowed(allowed);
}

void AudioStatsOf(const std::shared_ptr<void> &audio, tgn_audio_stats *out) {
  std::memset(out, 0, sizeof(*out));
  if (audio) std::static_pointer_cast<CallAudio>(audio)->Stats(out);
}
}  // namespace tgn

// ── the call ─────────────────────────────────────────────────────────────────────────────────────────────────────────
struct tgn_call {
  std::unique_ptr<tgcalls::Instance> instance;
  std::shared_ptr<CallAudio> audio;
  std::shared_ptr<tgn::RemoteVideo> remote = tgn::MakeRemoteVideo();   // tgcalls holds it weakly
  tgn_callbacks cb = {};
  std::atomic<bool> alive{true};   // false once stopping: late callbacks are dropped
  std::atomic<rtc::Thread *> own{nullptr};   // the thread tgcalls calls us on (V1: its own "WebRTC-Manager"), for Drain
  bool stopped = false;
};

extern "C" {

TGN_API int TGN_CALL tgn_init(tgn_log_fn log, void *user) {
  {
    std::lock_guard<std::mutex> lock(g_logMutex);
    g_log = log;
    g_logUser = user;
  }
  EnsureInit();
  return 0;
}

TGN_API int TGN_CALL tgn_version(void) { return (kVersionMajor << 16) | kVersionMinor; }

TGN_API int TGN_CALL tgn_max_layer(void) { EnsureInit(); return tgcalls::Meta::MaxLayer(); }

TGN_API int TGN_CALL tgn_versions(char *buf, int len) {
  EnsureInit();
  auto v = tgcalls::Meta::Versions();
  std::sort(v.begin(), v.end(), [](const std::string &a, const std::string &b) { return Numeric(a) > Numeric(b); });
  std::string s;
  for (auto &x : v) { if (!s.empty()) s += ';'; s += x; }
  CopyOut(s, buf, len);
  return (int)s.size();
}

TGN_API int TGN_CALL tgn_versions_api(char *buf, int len) {
  EnsureInit();
  auto v = tgcalls::Meta::Versions();
  std::reverse(v.begin(), v.end());
  std::string s;
  for (auto &x : v) { if (!s.empty()) s += ';'; s += x; }
  CopyOut(s, buf, len);
  return (int)s.size();
}

TGN_API tgn_call *TGN_CALL tgn_call_create(const tgn_call_params *p, const tgn_callbacks *cb) {
  if (!p || !p->key || !p->version) return nullptr;
  EnsureInit();
  try {
    auto call = std::make_unique<tgn_call>();
    if (cb) call->cb = *cb;
    call->audio = std::make_shared<CallAudio>(p->audio_mode, p->test_tone_hz > 0 ? p->test_tone_hz : 440);

    auto key = std::make_shared<std::array<uint8_t, tgcalls::EncryptionKey::kSize>>();
    std::memcpy(key->data(), p->key, key->size());

    std::vector<tgcalls::Endpoint> endpoints;
    for (int i = 0; i < p->endpoint_count; ++i) {
      const tgn_endpoint &e = p->endpoints[i];
      tgcalls::Endpoint ep;
      ep.endpointId = (int64_t)(((uint64_t)e.id_hi << 32) | e.id_lo);
      ep.host.ipv4 = e.ipv4;
      ep.host.ipv6 = e.ipv6;
      ep.port = (uint16_t)e.port;
      ep.type = e.tcp ? tgcalls::EndpointType::TcpRelay : tgcalls::EndpointType::UdpRelay;
      std::memcpy(ep.peerTag, e.peer_tag, 16);
      endpoints.push_back(ep);
    }
    std::vector<tgcalls::RtcServer> servers;
    for (int i = 0; i < p->server_count; ++i) {
      const tgn_rtc_server &s = p->servers[i];
      tgcalls::RtcServer rs;
      rs.id = (uint8_t)s.id;
      rs.host = s.host;
      rs.port = (uint16_t)s.port;
      rs.login = s.login;
      rs.password = s.password;
      rs.isTurn = s.is_turn != 0;
      rs.isTcp = s.is_tcp != 0;
      servers.push_back(rs);
    }

    tgcalls::Config config;
    config.initializationTimeout = 30.;   // tdesktop: kConnectTimeout / receive timeout
    config.receiveTimeout = 10.;
    config.dataSaving = tgcalls::DataSaving::Never;
    config.enableP2P = p->enable_p2p != 0;
    config.allowTCP = p->allow_tcp != 0;
    // The test tone is a steady sine: exactly what noise suppression removes. Real calls get the full processing.
    const bool processing = p->audio_mode == TGN_AUDIO_DEVICES;
    config.enableAEC = processing;
    config.enableNS = processing;
    config.enableAGC = processing;
    config.enableVolumeControl = true;
    config.maxApiLayer = p->max_layer;
    config.enableHighBitrateVideo = true;
    if (p->custom_parameters) config.customParameters = p->custom_parameters;

    tgn_call *raw = call.get();
    auto audio = call->audio;
    tgcalls::FakeAudioDeviceModule::Options options;
    options.samples_per_sec = kRate;
    options.num_channels = kChannels;
    options.scheduler_ = AudioScheduler;   // our threads: audio priority, a 1 ms timer (see AudioScheduler)

    tgcalls::Descriptor d{
        .version = p->version,
        .config = config,
        .endpoints = std::move(endpoints),
        .rtcServers = std::move(servers),
        .initialNetworkType = tgcalls::NetworkType::WiFi,
        .encryptionKey = tgcalls::EncryptionKey(key, p->is_outgoing != 0),
        .videoCapture = tgn::CaptureOf(p->camera),   // a video call sends from the start (V2 offers it at once)
        .stateUpdated = [raw](tgcalls::State s) {
          if (!raw->own.load()) raw->own = rtc::Thread::Current();
          if (raw->alive && raw->cb.state) raw->cb.state(raw->cb.user, (int32_t)s);
        },
        .signalBarsUpdated = [raw](int bars) {
          if (raw->alive && raw->cb.signal_bars) raw->cb.signal_bars(raw->cb.user, bars);
        },
        .audioLevelUpdated = [raw](float level) {
          if (raw->alive && raw->cb.audio_level) raw->cb.audio_level(raw->cb.user, level);
        },
        .remoteBatteryLevelIsLowUpdated = [raw](bool low) {
          if (raw->alive && raw->cb.remote_battery_low) raw->cb.remote_battery_low(raw->cb.user, low ? 1 : 0);
        },
        .remoteMediaStateUpdated = [raw](tgcalls::AudioState a, tgcalls::VideoState v) {
          if (raw->alive && raw->cb.remote_media) raw->cb.remote_media(raw->cb.user, (int32_t)a, (int32_t)v);
        },
        .signalingDataEmitted = [raw](const std::vector<uint8_t> &data) {
          if (!raw->own.load()) raw->own = rtc::Thread::Current();
          if (raw->alive && raw->cb.signaling) raw->cb.signaling(raw->cb.user, data.data(), (int32_t)data.size());
        },
        .createAudioDeviceModule = tgcalls::FakeAudioDeviceModule::Creator(
            std::make_shared<RendererAdapter>(audio), std::make_shared<RecorderAdapter>(audio), options),
    };
    call->instance = tgcalls::Meta::Create(p->version, std::move(d));
    if (!call->instance) {
      Log(std::string("tgn: no implementation for version ") + p->version);
      return nullptr;
    }
    tgn::AttachRemoteVideo(call->remote, *call->instance);   // once: V2 would deliver twice if attached twice
    return call.release();
  } catch (const std::exception &e) {
    Log(std::string("tgn: create failed: ") + e.what());
    return nullptr;
  }
}

TGN_API void TGN_CALL tgn_call_receive_signaling(tgn_call *c, const uint8_t *data, int32_t len) {
  if (!c || !c->instance || !data || len <= 0 || c->stopped) return;
  c->instance->receiveSignalingData(std::vector<uint8_t>(data, data + len));
}

TGN_API void TGN_CALL tgn_call_set_mute(tgn_call *c, int32_t mute) {
  if (c && c->instance && !c->stopped) c->instance->setMuteMicrophone(mute != 0);
}

TGN_API int TGN_CALL tgn_call_stop(tgn_call *c, char *debug_log, int32_t len, int32_t timeout_ms) {
  if (!c || !c->instance) return 0;
  if (c->stopped) { CopyOut("", debug_log, len); return 0; }
  c->stopped = true;
  tgn::SetRemoteVideo(*c->remote, nullptr, nullptr, 0, 0);   // no picture callback once stopping
  struct Done {
    std::mutex m;
    std::condition_variable cv;
    bool done = false;
    std::string log;
  };
  auto done = std::make_shared<Done>();
  c->instance->stop([done](tgcalls::FinalState fs) {
    std::lock_guard<std::mutex> lock(done->m);
    done->log = fs.debugLog;
    done->done = true;
    done->cv.notify_all();
  });
  std::unique_lock<std::mutex> lock(done->m);
  bool ok = done->cv.wait_for(lock, std::chrono::milliseconds(timeout_ms > 0 ? timeout_ms : 5000), [&] { return done->done; });
  c->alive = false;
  CopyOut(done->log, debug_log, len);
  return ok ? 0 : 1;
}

TGN_API void TGN_CALL tgn_call_destroy(tgn_call *c) {
  if (!c) return;
  if (!c->stopped) tgn_call_stop(c, nullptr, 0, 5000);
  c->alive = false;
  c->instance.reset();
  tgn::Drain(c->own.load());
  c->audio.reset();
  delete c;
}

TGN_API void TGN_CALL tgn_call_set_camera(tgn_call *c, tgn_camera *cam) {
  if (c && c->instance && !c->stopped) c->instance->setVideoCapture(tgn::CaptureOf(cam));
}

TGN_API void TGN_CALL tgn_call_set_remote_video(tgn_call *c, tgn_frame_fn cb, void *user, int32_t max_w, int32_t max_h) {
  if (c && c->remote) tgn::SetRemoteVideo(*c->remote, c->stopped ? nullptr : cb, user, max_w, max_h);
}

TGN_API int TGN_CALL tgn_call_video_stats(tgn_call *c, tgn_video_stats *out) {
  if (!c || !out || !c->remote) return -1;
  std::memset(out, 0, sizeof(*out));
  tgn::RemoteVideoStats(*c->remote, out);
  return 0;
}

TGN_API int TGN_CALL tgn_call_audio_stats(tgn_call *c, tgn_audio_stats *out) {
  if (!c || !out || !c->audio) return -1;
  std::memset(out, 0, sizeof(*out));
  c->audio->Stats(out);
  return 0;
}

// ── the loopback self-test ───────────────────────────────────────────────────────────────────────────────────────────
TGN_API int TGN_CALL tgn_selftest_loopback(int32_t seconds, char *report, int32_t len) {
  return tgn_selftest_audio(TGN_AUDIO_TEST, seconds, report, len);
}

TGN_API int TGN_CALL tgn_selftest_audio(int32_t b_mode, int32_t seconds, char *report, int32_t len) {
  EnsureInit();
  struct Side {
    tgn_call *call = nullptr;
    Side *peer = nullptr;
    std::atomic<int> state{-1};
    std::atomic<int> sent{0};
    std::mutex m;
    std::vector<std::vector<uint8_t>> inbox;   // handed over off the callback thread (never re-enter tgcalls from it)
  };
  Side a, b;
  a.peer = &b;
  b.peer = &a;
  auto onState = [](void *u, int32_t s) { ((Side *)u)->state = s; };
  auto onSignal = [](void *u, const uint8_t *d, int32_t n) {
    Side *self = (Side *)u;
    self->sent++;
    std::lock_guard<std::mutex> lock(self->peer->m);
    self->peer->inbox.emplace_back(d, d + n);
  };
  char versions[256];
  tgn_versions(versions, sizeof(versions));
  std::string version = std::string(versions).substr(0, std::string(versions).find(';'));
  uint8_t key[256];
  for (int i = 0; i < 256; ++i) key[i] = (uint8_t)(i * 13 + 7);

  auto make = [&](Side &s, bool outgoing, int hz, int mode) {
    tgn_call_params p = {};
    p.version = version.c_str();
    p.key = key;
    p.is_outgoing = outgoing;
    p.enable_p2p = 1;
    p.max_layer = tgn_max_layer();
    p.audio_mode = mode;
    p.test_tone_hz = hz;
    tgn_callbacks cb = {};
    cb.user = &s;
    cb.state = onState;
    cb.signaling = onSignal;
    s.call = tgn_call_create(&p, &cb);
  };
  make(a, true, 440, TGN_AUDIO_TEST);
  make(b, false, 1000, b_mode);   // TGN_AUDIO_SPEAKER_TEST: B plays what it hears through this PC's speaker
  std::string r = "version " + version + "\n";
  if (!a.call || !b.call) {
    r += "create failed\n";
    CopyOut(r, report, len);
    if (a.call) tgn_call_destroy(a.call);
    if (b.call) tgn_call_destroy(b.call);
    return 2;
  }
  auto deliver = [](Side &s) {
    std::vector<std::vector<uint8_t>> msgs;
    {
      std::lock_guard<std::mutex> lock(s.m);
      msgs.swap(s.inbox);
    }
    for (auto &m : msgs) tgn_call_receive_signaling(s.call, m.data(), (int32_t)m.size());
  };
  auto start = std::chrono::steady_clock::now();
  bool heard = false;
  tgn_audio_stats sa = {}, sb = {};
  while (std::chrono::steady_clock::now() - start < std::chrono::seconds(seconds > 0 ? seconds : 20)) {
    deliver(a);
    deliver(b);
    tgn_call_audio_stats(a.call, &sa);
    tgn_call_audio_stats(b.call, &sb);
    // each side hears the OTHER's tone: A sends 440, B sends 1000
    if (a.state == 2 && b.state == 2 && sa.frames_audible > 50 && sb.frames_audible > 50 &&
        std::abs(sa.rendered_hz - 1000) < 60 && std::abs(sb.rendered_hz - 440) < 30) {
      heard = true;
      if (b_mode == TGN_AUDIO_TEST) break;
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
  }
  auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count();
  char line[512];
  std::snprintf(line, sizeof(line),
                "after %lld ms: A state %d sent %d signaling, hears %d Hz (%d audible of %d frames, rms %d); "
                "B state %d sent %d signaling, hears %d Hz (%d audible of %d frames, rms %d)\n",
                (long long)ms, a.state.load(), a.sent.load(), sa.rendered_hz, sa.frames_audible, sa.frames_rendered,
                sa.rendered_rms, b.state.load(), b.sent.load(), sb.rendered_hz, sb.frames_audible, sb.frames_rendered,
                sb.rendered_rms);
  r += line;
  if (b_mode == TGN_AUDIO_SPEAKER_TEST) {
    std::snprintf(line, sizeof(line), "B's speaker: %d gaps (underruns) in %lld ms, %d capture overruns\n", sb.playback_underruns,
                  (long long)ms, sb.capture_overruns);
    r += line;
  }
  int sta = tgn_call_stop(a.call, nullptr, 0, 5000), stb = tgn_call_stop(b.call, nullptr, 0, 5000);
  r += std::string("stop: A ") + (sta ? "TIMED OUT" : "ok") + ", B " + (stb ? "TIMED OUT" : "ok") + "\n";
  tgn_call_destroy(a.call);
  tgn_call_destroy(b.call);
  r += heard ? "PASS\n" : "FAIL\n";
  CopyOut(r, report, len);
  return heard && !sta && !stb ? 0 : 1;
}

}  // extern "C"
