// tgcalls_native: the call audio (WinMM through tgcalls' FakeAudioDeviceModule, tgcalls_native.cpp) for the other
// translation units that drive an instance (tgcalls_group.cpp). Not part of the C ABI.
#ifndef TGCALLS_AUDIO_H
#define TGCALLS_AUDIO_H

#include <functional>
#include <memory>

#include "api/scoped_refptr.h"
#include "tgcalls_native.h"

namespace webrtc {
class AudioDeviceModule;
class TaskQueueFactory;
}  // namespace webrtc

namespace tgn {

// A call's audio (opaque): mode is TGN_AUDIO_*; with micAllowed false the microphone stays closed (the recorder says
// "nothing yet") until SetMicAllowed(true) - a group call is joined muted, and tgcalls polls the recorder from the start.
std::shared_ptr<void> MakeCallAudio(int mode, int toneHz, bool micAllowed);
// The audio device for a descriptor's createAudioDeviceModule: tgcalls' FakeAudioDeviceModule over this audio, its loops
// on our AudioScheduler. A new one per instance (the Creator refuses a second call).
std::function<webrtc::scoped_refptr<webrtc::AudioDeviceModule>(webrtc::TaskQueueFactory *)> AudioDeviceCreator(
    const std::shared_ptr<void> &audio);
void SetMicAllowed(const std::shared_ptr<void> &audio, bool allowed);
void AudioStatsOf(const std::shared_ptr<void> &audio, tgn_audio_stats *out);

}  // namespace tgn

#endif
