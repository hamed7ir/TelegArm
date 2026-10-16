// tgcalls_native group calls (voice / video chats): tgcalls' GroupInstanceCustomImpl behind the tgn_group_* C ABI, with
// the 1:1 calls' audio (WinMM through FakeAudioDeviceModule, our AudioScheduler) and cameras. Same compiler settings as
// tgcalls_native.cpp (the BUILD-INFO defines are ABI-relevant).
#include <winsock2.h>
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "tgcalls/StaticThreads.h"
#include "tgcalls/VideoCaptureInterface.h"
#include "tgcalls/group/GroupInstanceCustomImpl.h"
#include "tgcalls/group/GroupJoinPayloadInternal.h"

#include "api/video/video_frame.h"
#include "api/video/video_sink_interface.h"

#include "tgcalls_audio.h"
#include "tgcalls_internal.h"

namespace tgn {
std::shared_ptr<rtc::VideoSinkInterface<webrtc::VideoFrame>> SinkOf(const std::shared_ptr<RemoteVideo> &remote);
}  // namespace tgn

struct tgn_group {
  std::unique_ptr<tgcalls::GroupInstanceCustomImpl> instance;
  std::shared_ptr<void> audio;
  tgn_group_callbacks cb = {};
  std::atomic<bool> alive{true};   // false once stopping: late callbacks are dropped
  bool stopped = false;
  std::mutex videoMutex;
  std::map<std::string, std::shared_ptr<tgn::RemoteVideo>> outputs;   // tgcalls holds these weakly
};

extern "C" {

TGN_API tgn_group *TGN_CALL tgn_group_create(const tgn_group_params *p, const tgn_group_callbacks *cb) {
  if (!p) return nullptr;
  tgn::EnsureInit();
  try {
    auto g = std::make_unique<tgn_group>();
    if (cb) g->cb = *cb;
    // joined muted: the microphone opens on the first unmute (tgcalls polls the recorder from the start); a test tone
    // needs no microphone, so the test modes run from the start
    g->audio = tgn::MakeCallAudio(p->audio_mode, p->test_tone_hz, p->audio_mode != TGN_AUDIO_DEVICES);
    tgn_group *raw = g.get();
    tgcalls::GroupInstanceDescriptor d;
    d.threads = tgcalls::StaticThreads::getThreads();
    d.config.need_log = false;   // else every WebRTC INFO line is kept in memory for the whole call (LogSinkImpl)
    d.networkStateUpdated = [raw](tgcalls::GroupNetworkState s) {
      if (raw->alive && raw->cb.network) raw->cb.network(raw->cb.user, s.isConnected ? 1 : 0, (int32_t)s.connectionMode);
    };
    d.signalBarsUpdated = [raw](int bars) {
      if (raw->alive && raw->cb.signal_bars) raw->cb.signal_bars(raw->cb.user, bars);
    };
    // ALWAYS set: without it tgcalls never marks our packets as speech, and nobody sees us speaking
    d.audioLevelsUpdated = [raw](tgcalls::GroupLevelsUpdate const &u) {
      if (!raw->alive || !raw->cb.levels || u.updates.empty()) return;
      std::vector<tgn_group_level> v;
      v.reserve(u.updates.size());
      for (auto &x : u.updates)
        v.push_back(tgn_group_level{x.ssrc, (int32_t)(x.value.level * 1000.f), x.value.voice ? 1 : 0, x.value.isMuted ? 1 : 0});
      raw->cb.levels(raw->cb.user, v.data(), (int32_t)v.size());
    };
    d.createAudioDeviceModule = tgn::AudioDeviceCreator(g->audio);
    d.videoContentType = p->video ? tgcalls::VideoContentType::Generic : tgcalls::VideoContentType::None;
    d.initialEnableNoiseSuppression = p->noise_suppression != 0;
    if (p->prefer_vp8) d.videoCodecPreferences = {tgcalls::VideoCodecName::VP8};
    if (p->camera) d.videoCapture = tgn::CaptureOf(p->camera);
    g->instance = std::make_unique<tgcalls::GroupInstanceCustomImpl>(std::move(d));
    return g.release();
  } catch (const std::exception &e) {
    tgn::Log(std::string("tgn: group create failed: ") + e.what());
    return nullptr;
  }
}

TGN_API void TGN_CALL tgn_group_emit_join(tgn_group *g) {
  if (!g || !g->instance || g->stopped) return;
  // on tgcalls' network thread; the destroy path drains it before g is freed, and alive is false by then
  g->instance->emitJoinPayload([g](tgcalls::GroupJoinPayload const &payload) {
    if (g->alive && g->cb.join_payload) g->cb.join_payload(g->cb.user, payload.audioSsrc, payload.json.c_str(), (int32_t)payload.json.size());
  });
}

TGN_API int TGN_CALL tgn_group_set_join_response(tgn_group *g, const char *json) {
  if (!g || !g->instance || !json || g->stopped) return 1;
  std::string s(json);
  if (!tgcalls::GroupJoinResponsePayload::parse(s)) {   // tgcalls would only log it and never connect
    tgn::Log("tgn: group join response not understood (" + std::to_string(s.size()) + " bytes)");
    return 1;
  }
  g->instance->setJoinResponsePayload(s);
  return 0;
}

TGN_API void TGN_CALL tgn_group_set_mode(tgn_group *g, int32_t mode) {
  if (!g || !g->instance || g->stopped) return;
  g->instance->setConnectionMode(mode == TGN_GROUP_MODE_RTC ? tgcalls::GroupConnectionMode::GroupConnectionModeRtc
                                                            : tgcalls::GroupConnectionMode::GroupConnectionModeNone,
                                 false, false);
}

TGN_API void TGN_CALL tgn_group_set_mute(tgn_group *g, int32_t mute) {
  if (!g || !g->instance || g->stopped) return;
  if (!mute) tgn::SetMicAllowed(g->audio, true);   // the microphone opens on the first unmute and stays open
  g->instance->setIsMuted(mute != 0);
}

TGN_API void TGN_CALL tgn_group_set_camera(tgn_group *g, tgn_camera *cam) {
  if (g && g->instance && !g->stopped) g->instance->setVideoCapture(tgn::CaptureOf(cam));
}

TGN_API void TGN_CALL tgn_group_set_video_channels(tgn_group *g, const tgn_group_video *v, int32_t count) {
  if (!g || !g->instance || g->stopped) return;
  std::vector<tgcalls::VideoChannelDescription> list;
  for (int i = 0; v && i < count; ++i) {
    tgcalls::VideoChannelDescription c;
    c.audioSsrc = v[i].audio_ssrc;
    c.userId = (int64_t)(((uint64_t)v[i].user_hi << 32) | v[i].user_lo);
    c.endpointId = std::string(v[i].endpoint, strnlen(v[i].endpoint, sizeof(v[i].endpoint)));
    for (int k = 0; k < v[i].group_count && k < 4; ++k) {
      tgcalls::MediaSsrcGroup sg;
      sg.semantics = std::string(v[i].groups[k].semantics, strnlen(v[i].groups[k].semantics, sizeof(v[i].groups[k].semantics)));
      for (int j = 0; j < v[i].groups[k].count && j < 8; ++j) sg.ssrcs.push_back(v[i].groups[k].ssrc[j]);
      c.ssrcGroups.push_back(sg);
    }
    auto q = [](int32_t x) {
      return x >= TGN_QUALITY_FULL ? tgcalls::VideoChannelDescription::Quality::Full
             : x == TGN_QUALITY_MEDIUM ? tgcalls::VideoChannelDescription::Quality::Medium
                                       : tgcalls::VideoChannelDescription::Quality::Thumbnail;
    };
    c.minQuality = q(v[i].min_quality);
    c.maxQuality = q(v[i].max_quality);
    list.push_back(c);
  }
  g->instance->setRequestedVideoChannels(std::move(list));
}

TGN_API void TGN_CALL tgn_group_set_video_output(tgn_group *g, const char *endpoint, tgn_frame_fn cb, void *user, int32_t max_w,
                                                 int32_t max_h, int32_t max_fps) {
  if (!g || !g->instance || !endpoint) return;
  std::shared_ptr<tgn::RemoteVideo> remote;
  bool attach = false;
  {
    std::lock_guard<std::mutex> lock(g->videoMutex);
    auto it = g->outputs.find(endpoint);
    if (it == g->outputs.end()) {
      if (!cb || g->stopped) return;
      remote = tgn::MakeRemoteVideo();
      g->outputs[endpoint] = remote;
      attach = true;
    } else {
      remote = it->second;
    }
  }
  tgn::SetRemoteVideo(*remote, g->stopped ? nullptr : cb, user, max_w, max_h, max_fps);
  if (attach) g->instance->addIncomingVideoOutput(endpoint, tgn::SinkOf(remote));   // once per endpoint; tgcalls keeps it weakly
}

TGN_API void TGN_CALL tgn_group_set_volume(tgn_group *g, uint32_t ssrc, int32_t permille) {
  if (g && g->instance && !g->stopped) g->instance->setVolume(ssrc, std::max(0, permille) / 1000.0);
}

TGN_API int TGN_CALL tgn_group_audio_stats(tgn_group *g, tgn_audio_stats *out) {
  if (!g || !out) return -1;
  tgn::AudioStatsOf(g->audio, out);
  return 0;
}

TGN_API int TGN_CALL tgn_group_stop(tgn_group *g, int32_t timeout_ms) {
  if (!g || !g->instance) return 0;
  if (g->stopped) return 0;
  g->stopped = true;
  {
    std::lock_guard<std::mutex> lock(g->videoMutex);
    for (auto &o : g->outputs) tgn::SetRemoteVideo(*o.second, nullptr, nullptr, 0, 0, 0);   // no picture once stopping
  }
  struct Done {
    std::mutex m;
    std::condition_variable cv;
    bool done = false;
  };
  auto done = std::make_shared<Done>();
  g->instance->stop([done] {
    std::lock_guard<std::mutex> lock(done->m);
    done->done = true;
    done->cv.notify_all();
  });
  std::unique_lock<std::mutex> lock(done->m);
  bool ok = done->cv.wait_for(lock, std::chrono::milliseconds(timeout_ms > 0 ? timeout_ms : 5000), [&] { return done->done; });
  g->alive = false;
  return ok ? 0 : 1;
}

TGN_API void TGN_CALL tgn_group_destroy(tgn_group *g) {
  if (!g) return;
  if (!g->stopped) tgn_group_stop(g, 5000);
  g->alive = false;
  g->instance.reset();   // blocks briefly on the media thread; its network manager goes later, on the network thread...
  tgn::Drain(nullptr);   // ...so finish that now (left over, it hung process exit like the V1 1:1 calls - see Drain)
  {
    std::lock_guard<std::mutex> lock(g->videoMutex);
    g->outputs.clear();
  }
  g->audio.reset();
  delete g;
}

// ── the offline self-test ────────────────────────────────────────────────────────────────────────────────────────────
namespace {

struct Probe {
  std::mutex m;
  std::condition_variable cv;
  uint32_t ssrc = 0;
  std::string json;
  int payloads = 0;
  std::atomic<int> levels{0}, ownLevels{0}, ownMuted{0}, ownUnmutedAudible{0}, network{0};
};

void TGN_CALL OnPayload(void *u, uint32_t ssrc, const char *json, int32_t len) {
  auto *p = (Probe *)u;
  std::lock_guard<std::mutex> lock(p->m);
  p->ssrc = ssrc;
  p->json.assign(json, json + len);
  p->payloads++;
  p->cv.notify_all();
}

void TGN_CALL OnLevels(void *u, const tgn_group_level *items, int32_t count) {
  auto *p = (Probe *)u;
  p->levels++;
  for (int i = 0; i < count; ++i)
    if (items[i].ssrc == 0) {
      p->ownLevels++;
      if (items[i].muted) p->ownMuted++;
      else if (items[i].level_milli > 20) p->ownUnmutedAudible++;
    }
}

void TGN_CALL OnNetwork(void *u, int32_t, int32_t) { ((Probe *)u)->network++; }

bool WaitPayload(Probe &p, int before, int ms) {
  std::unique_lock<std::mutex> lock(p.m);
  return p.cv.wait_for(lock, std::chrono::milliseconds(ms), [&] { return p.payloads > before; });
}

int Count(const std::string &s, const char *what) {
  int n = 0;
  for (size_t at = s.find(what); at != std::string::npos; at = s.find(what, at + 1)) n++;
  return n;
}

}  // namespace

TGN_API int TGN_CALL tgn_selftest_group(int32_t flags, char *report, int32_t len) {
  (void)flags;
  tgn::EnsureInit();
  std::string r;
  bool pass = true;
  char line[640];
  auto check = [&](bool ok, const std::string &what) {
    r += (ok ? "ok    " : "FAIL  ") + what + "\n";
    if (!ok) pass = false;
  };
  for (int video = 0; video <= 1; ++video) {
    Probe probe;
    tgn_group_params p = {};
    p.audio_mode = TGN_AUDIO_TEST;
    p.test_tone_hz = 440;
    p.video = video;
    tgn_group_callbacks cb = {};
    cb.user = &probe;
    cb.join_payload = OnPayload;
    cb.levels = OnLevels;
    cb.network = OnNetwork;
    auto t0 = std::chrono::steady_clock::now();
    tgn_group *g = tgn_group_create(&p, &cb);
    check(g != nullptr, std::string(video ? "video" : "voice") + " instance created");
    if (!g) continue;
    tgn_group_emit_join(g);
    bool got = WaitPayload(probe, 0, 5000);
    std::string json;
    uint32_t ssrc;
    {
      std::lock_guard<std::mutex> lock(probe.m);
      json = probe.json;
      ssrc = probe.ssrc;
    }
    std::snprintf(line, sizeof(line), "join payload in %lld ms: ssrc %u, %d bytes",
                  (long long)std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count(), ssrc,
                  (int)json.size());
    check(got && ssrc != 0, line);
    check(json.find("\"ufrag\"") != std::string::npos && json.find("\"pwd\"") != std::string::npos &&
              json.find("\"fingerprints\"") != std::string::npos && json.find("\"passive\"") != std::string::npos &&
              json.find("\"ssrc\"") != std::string::npos,
          "payload has ufrag, pwd, a passive fingerprint, the ssrc");
    if (video) check(Count(json, "\"SIM\"") == 1 && Count(json, "\"FID\"") == 3, "video payload: 1 SIM + 3 FID groups (3 simulcast layers)");
    else check(json.find("ssrc-groups") == std::string::npos, "voice payload: no ssrc-groups");

    // levels: every 100 ms, ssrc 0 = us, muted until we unmute; then the tone is heard as our own level
    std::this_thread::sleep_for(std::chrono::milliseconds(600));
    int lv = probe.levels, muted = probe.ownMuted;
    check(lv >= 3 && muted >= 1, "levels every ~100 ms (" + std::to_string(lv) + " in 0.6 s), our own entry says muted");
    tgn_group_set_mute(g, 0);
    std::this_thread::sleep_for(std::chrono::milliseconds(800));
    check(probe.ownUnmutedAudible >= 1, "unmuted: our own level shows the test tone (" + std::to_string(probe.ownUnmutedAudible.load()) + ")");

    // a rejoin makes new keys and a new ssrc
    tgn_group_set_mode(g, TGN_GROUP_MODE_NONE);
    tgn_group_emit_join(g);
    bool again = WaitPayload(probe, 1, 5000);
    uint32_t ssrc2;
    {
      std::lock_guard<std::mutex> lock(probe.m);
      ssrc2 = probe.ssrc;
    }
    check(again && ssrc2 != 0 && ssrc2 != ssrc, "rejoin: a new ssrc (" + std::to_string(ssrc2) + ")");

    // the server's answer: malformed refused, a well-formed one taken (no server here: it never connects)
    check(tgn_group_set_join_response(g, "{\"nonsense\": 1}") == 1, "a malformed join response is refused");
    const char *answer =
        "{\"transport\":{\"ufrag\":\"abcd\",\"pwd\":\"0123456789abcdef01234567\",\"fingerprints\":[{\"hash\":\"sha-256\",\"setup\":"
        "\"active\",\"fingerprint\":\"AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99\"}],"
        "\"candidates\":[{\"port\":\"5000\",\"protocol\":\"udp\",\"network\":\"0\",\"generation\":\"0\",\"id\":\"1\",\"component\":\"1\","
        "\"foundation\":\"1\",\"priority\":\"2130706431\",\"ip\":\"127.0.0.1\",\"type\":\"host\"}]}}";
    check(tgn_group_set_join_response(g, answer) == 0, "a well-formed join response is taken");
    tgn_group_set_mode(g, TGN_GROUP_MODE_RTC);
    std::this_thread::sleep_for(std::chrono::milliseconds(300));

    auto t1 = std::chrono::steady_clock::now();
    int stop = tgn_group_stop(g, 5000);
    tgn_group_destroy(g);
    std::snprintf(line, sizeof(line), "stopped and destroyed in %lld ms",
                  (long long)std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t1).count());
    check(stop == 0, line);
  }
  r += pass ? "PASS\n" : "FAIL\n";
  if (report && len > 0) std::snprintf(report, len, "%s", r.c_str());
  return pass ? 0 : 1;
}

}  // extern "C"
