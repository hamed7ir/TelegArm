// tgcalls_link_test: links tgcalls + tg_owt + every dependency into one exe (the ARM32/x86 link check).
// Plain run: registers the call implementations and prints the protocol versions and max layer.
// "--construct" (device smoke test): creates a 1:1 call instance and a group-call instance on a fake audio device,
// asks the group instance for its join payload, then stops both. No network peer is involved.
// A host MUST initialise Winsock before using tgcalls: WebRTC's socket server does not call WSAStartup (tdesktop gets
// it from Qt). Without it every UDP socket fails ("UDP socket creation failed") and no ICE candidate is gathered.
#include <winsock2.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "tgcalls/Instance.h"
#include "tgcalls/group/GroupInstanceCustomImpl.h"
#include "tgcalls/FakeAudioDeviceModule.h"
#include "tgcalls/StaticThreads.h"

// Like tdesktop (Telegram/SourceFiles/calls/calls_call.cpp): the implementation headers are not public (they use
// tgcalls-internal include paths), so only declare the classes and instantiate Register<> against them.
namespace tgcalls {
class InstanceImpl;
class InstanceV2Impl;
class InstanceV2ReferenceImpl;
}  // namespace tgcalls

namespace {

struct Waiter {
  std::mutex m;
  std::condition_variable cv;
  bool done = false;
  void Signal() {
    std::lock_guard<std::mutex> lock(m);
    done = true;
    cv.notify_all();
  }
  bool Wait(int seconds) {
    std::unique_lock<std::mutex> lock(m);
    return cv.wait_for(lock, std::chrono::seconds(seconds), [&] { return done; });
  }
};

int Construct() {
  // 1:1 call, numerically newest protocol version (Meta::Versions() is sorted as strings: "9.0.0" > "13.0.0").
  auto versions = tgcalls::Meta::Versions();
  const auto numeric = [](const std::string &v) {
    int a = 0, b = 0, c = 0;
    std::sscanf(v.c_str(), "%d.%d.%d", &a, &b, &c);
    return std::array<int, 3>{a, b, c};
  };
  std::sort(versions.begin(), versions.end(),
            [&](const std::string &x, const std::string &y) { return numeric(x) < numeric(y); });
  const std::string version = versions.empty() ? std::string() : versions.back();
  auto key = std::make_shared<std::array<uint8_t, tgcalls::EncryptionKey::kSize>>();
  for (size_t i = 0; i < key->size(); ++i) (*key)[i] = uint8_t(i * 7 + 1);
  tgcalls::Descriptor d{
      .version = version,
      .encryptionKey = tgcalls::EncryptionKey(key, true),
      .stateUpdated = [](tgcalls::State s) { std::printf("p2p state %d\n", int(s)); },
      .signalingDataEmitted = [](const std::vector<uint8_t> &data) {
        std::printf("p2p signaling %zu bytes\n", data.size());
      },
      .createAudioDeviceModule = tgcalls::FakeAudioDeviceModule::Creator(nullptr, nullptr, {}),
  };
  auto instance = tgcalls::Meta::Create(version, std::move(d));
  std::printf("p2p instance %s (version %s)\n", instance ? "created" : "NOT created", version.c_str());
  if (instance) {
    std::this_thread::sleep_for(std::chrono::seconds(2));
    Waiter w;
    instance->stop([&w](tgcalls::FinalState) { w.Signal(); });
    std::printf("p2p stop %s\n", w.Wait(10) ? "completed" : "TIMED OUT");
    instance.reset();
  }

  // Group call.
  tgcalls::GroupInstanceDescriptor g;
  g.threads = tgcalls::StaticThreads::getThreads();
  g.networkStateUpdated = [](tgcalls::GroupNetworkState s) {
    std::printf("group network connected=%d\n", int(s.isConnected));
  };
  g.createAudioDeviceModule = tgcalls::FakeAudioDeviceModule::Creator(nullptr, nullptr, {});
  auto group = std::make_unique<tgcalls::GroupInstanceCustomImpl>(std::move(g));
  Waiter jp;
  group->emitJoinPayload([&jp](tgcalls::GroupJoinPayload const &payload) {
    std::printf("group join payload: ssrc %u, %zu bytes json\n", unsigned(payload.audioSsrc), payload.json.size());
    jp.Signal();
  });
  std::printf("group join payload %s\n", jp.Wait(10) ? "received" : "TIMED OUT");
  Waiter gs;
  group->stop([&gs] { gs.Signal(); });
  std::printf("group stop %s\n", gs.Wait(10) ? "completed" : "TIMED OUT");
  group.reset();
  return 0;
}

}  // namespace

int main(int argc, char **argv) {
  WSADATA wsa;
  if (const int rc = WSAStartup(MAKEWORD(2, 2), &wsa)) {
    std::printf("WSAStartup failed: %d\n", rc);
    return 1;
  }
  tgcalls::Register<tgcalls::InstanceImpl>();
  tgcalls::Register<tgcalls::InstanceV2Impl>();
  tgcalls::Register<tgcalls::InstanceV2ReferenceImpl>();
  tgcalls::SetLoggingFunction([](std::string const &s) { std::fprintf(stderr, "%s\n", s.c_str()); });

  std::printf("tgcalls max layer %d, versions:", tgcalls::Meta::MaxLayer());
  for (const auto &v : tgcalls::Meta::Versions()) std::printf(" %s", v.c_str());
  std::printf("\n");
  if (argc > 1 && std::string(argv[1]) == "--construct") return Construct();
  return 0;
}
