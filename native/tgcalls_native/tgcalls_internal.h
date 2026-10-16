// tgcalls_native internals shared by its translation units (tgcalls_native.cpp: calls and audio; tgcalls_video.cpp:
// the platform, cameras, remote video, the video self-test; tgcalls_camsink.cpp: the Windows 8.1 media sink).
#ifndef TGCALLS_INTERNAL_H
#define TGCALLS_INTERNAL_H

#include <atomic>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "tgcalls_native.h"

namespace rtc {
class Thread;
}  // namespace rtc

namespace tgcalls {
class Instance;
class VideoCaptureInterface;
}  // namespace tgcalls

namespace tgn {

void Log(const std::string &line);
void EnsureInit();
void Drain(rtc::Thread *own);   // waits until tgcalls' threads ran everything posted so far (teardown)

// The camera behind a tgn_camera: shared with the media sink, so a late sample after tgn_camera_destroy finds a
// closed camera instead of freed memory.
class CameraCore;
std::shared_ptr<CameraCore> CoreOf(tgn_camera *cam);
int Push(CameraCore &core, const tgn_frame_in &f);
std::shared_ptr<tgcalls::VideoCaptureInterface> CaptureOf(tgn_camera *cam);

// The remote video of one call: attached to the instance once (V2 appends a sink on every setIncomingVideoOutput, so
// setting it twice would deliver each picture twice); the app's callback can be swapped any time.
class RemoteVideo;
std::shared_ptr<RemoteVideo> MakeRemoteVideo();
void AttachRemoteVideo(const std::shared_ptr<RemoteVideo> &remote, tgcalls::Instance &instance);
void SetRemoteVideo(RemoteVideo &remote, tgn_frame_fn cb, void *user, int max_w, int max_h, int max_fps = 0);
void RemoteVideoStats(RemoteVideo &remote, tgn_video_stats *out);

}  // namespace tgn

#endif
