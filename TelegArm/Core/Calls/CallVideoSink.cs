using System;
using System.Runtime.InteropServices;
using System.Threading;
using TelegArm.Core.Media;

namespace TelegArm.Core.Calls
{
    /// <summary>
    /// Pictures out of tgcalls_native (the local preview or the remote video) into a <see cref="VideoFrameStore"/> the UI
    /// paints. The native side calls <see cref="Callback"/> on its own thread, one picture at a time; the delegate lives as
    /// long as this object, and the call / camera stops calling it before it lets go.
    /// </summary>
    public sealed class CallVideoSink
    {
        public readonly VideoFrameStore Store = new VideoFrameStore();
        internal readonly TgCalls.FrameCallback Callback;
        private int _frames;
        private volatile bool _released;

        /// <summary>A new picture is in <see cref="Store"/> (raised on the producing thread: coalesce, then repaint).</summary>
        public event Action FrameReady;
        public int Frames { get { return _frames; } }

        public CallVideoSink() { Callback = OnFrame; }

        private void OnFrame(IntPtr user, IntPtr bgra, int w, int h, int stride)
        {
            if (_released || bgra == IntPtr.Zero || w <= 0 || h <= 0 || stride < w * 4) return;
            Store.EnsureBack(w, h);
            if (stride == w * 4) Marshal.Copy(bgra, Store.Back, 0, w * h * 4);
            else for (int y = 0; y < h; y++) Marshal.Copy(bgra + y * stride, Store.Back, y * w * 4, w * 4);
            Store.Swap();
            Interlocked.Increment(ref _frames);
            var h2 = FrameReady;
            if (h2 != null) h2();
        }

        /// <summary>The call is over and nothing calls back any more: drop the picture and unpin the buffer the store keeps
        /// pinned for the producer (a pinned array is a GC root: it would outlive the call).</summary>
        public void Release()
        {
            _released = true;
            lock (Store.Sync) { Store.Front = null; Store.FrontWidth = Store.FrontHeight = 0; Store.Serial++; Store.Free(); }
        }

        /// <summary>Forget the last picture (the camera went off): the UI shows the avatar again.</summary>
        public void Clear() { lock (Store.Sync) { Store.Front = null; Store.FrontWidth = Store.FrontHeight = 0; Store.Serial++; } }
    }
}
