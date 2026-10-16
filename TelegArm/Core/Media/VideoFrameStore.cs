using System.Runtime.InteropServices;

namespace TelegArm.Core.Media
{
    /// <summary>
    /// The newest video frame, shared between the thread that makes it (the player's video thread, a call's camera or
    /// remote video: writes the back buffer, swaps) and the UI (paints the front one under <see cref="Sync"/>). BGRA,
    /// 32 bpp, top-down, stride = width * 4.
    /// </summary>
    public sealed class VideoFrameStore
    {
        public readonly object Sync = new object();
        public byte[] Front;
        public int FrontWidth, FrontHeight;
        public int Serial;                 // bumps with every new frame
        internal byte[] Back;
        internal GCHandle BackPin;
        internal int BackWidth, BackHeight;

        internal void EnsureBack(int w, int h)
        {
            if (Back != null && BackWidth == w && BackHeight == h) return;
            if (BackPin.IsAllocated) BackPin.Free();
            Back = new byte[w * h * 4];
            BackPin = GCHandle.Alloc(Back, GCHandleType.Pinned);
            BackWidth = w; BackHeight = h;
        }

        internal void Swap()
        {
            lock (Sync)
            {
                var b = Front; int bw = FrontWidth, bh = FrontHeight;
                Front = Back; FrontWidth = BackWidth; FrontHeight = BackHeight;
                if (BackPin.IsAllocated) BackPin.Free();
                Back = b; BackWidth = bw; BackHeight = bh;
                if (Back != null) BackPin = GCHandle.Alloc(Back, GCHandleType.Pinned);
                Serial++;
            }
        }

        internal void Free() { if (BackPin.IsAllocated) BackPin.Free(); }
    }
}
