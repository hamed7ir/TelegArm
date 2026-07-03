using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LibVLCSharp.Shared;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Renders a Telegram WebM video sticker (VP9, OPAQUE — the transparent ones are all TGS/rlottie) as a
    /// looping animation, decoding through libVLC's raw frame callbacks. VLC decodes on its own threads; each
    /// (fps-capped) frame is copied into a FRESH 32bpp bitmap and marshaled to the host via BeginInvoke (the
    /// GDI+ blit stays on the UI thread).
    ///
    /// LOOPING (WEBM-playfix2): the clip is looped by restarting on <see cref="MediaPlayer.EndReached"/>
    /// (marshaled off VLC's event thread) — NOT via <c>:input-repeat</c>, whose internal re-demux on this
    /// build re-opened the matroska in a bad state ("cannot find any cluster / damaged file"). A shared,
    /// app-lifetime <see cref="LibVLC"/> avoids the per-play create/dispose churn that corrupted re-opens.
    ///
    /// DISPOSE-vs-RENDER FENCE (kept from WEBM-robust): VLC writes the decoded frame into our pinned buffer
    /// BETWEEN the Lock and Unlock callbacks, on VLC's thread, OUTSIDE any lock we hold. Lock/Unlock bracket
    /// an <c>_inFlight</c> counter around exactly that write window; Dispose marks stopped, Stop()s the player,
    /// WAITS for _inFlight to drain, and only THEN frees the buffer — no write can touch freed memory.
    /// </summary>
    public sealed class WebmAnimator : IDisposable
    {
        private static LibVLC _sharedVlc;                                   // one app-lifetime instance — no per-play churn
        private static readonly object _vlcGate = new object();

        private static LibVLC SharedVlc()
        {
            if (_sharedVlc == null)
                lock (_vlcGate)
                    if (_sharedVlc == null)
                        _sharedVlc = new LibVLC("--no-audio", "--quiet", "--no-stats", "--no-osd");
            return _sharedVlc;
        }

        /// <summary>Decodes ONE frame of a video file into a Bitmap (via the shared LibVLC + a raw frame
        /// callback) — the static thumbnail for a GIF that has NO document thumb (what official Telegram does).
        /// BLOCKING (waits for the first frame ≤ timeout); call it OFF the UI thread. Null on failure.</summary>
        public static Bitmap GrabFirstFrame(string path, int w, int h, int timeoutMs = 4000)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            if (!TelegArm.Core.VlcEnvironment.TryInitialize()) return null;
            if (w <= 0 || w > 1024) w = 320;
            if (h <= 0 || h > 1024) h = 240;
            int stride = w * 4;
            byte[] buf = new byte[stride * h];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            IntPtr bufPtr = handle.AddrOfPinnedObject();
            bool got = false;
            MediaPlayer.LibVLCVideoLockCb lockCb = (o, planes) => { Marshal.WriteIntPtr(planes, bufPtr); return bufPtr; };
            MediaPlayer.LibVLCVideoUnlockCb unlockCb = (o, p, pl) => { };
            MediaPlayer.LibVLCVideoDisplayCb displayCb = (o, p) => { got = true; };
            MediaPlayer mp = null; Media media = null;
            try
            {
                mp = new MediaPlayer(SharedVlc());
                mp.SetVideoFormat("RV32", (uint)w, (uint)h, (uint)stride);
                mp.SetVideoCallbacks(lockCb, unlockCb, displayCb);
                media = new Media(SharedVlc(), path, FromType.FromPath);
                mp.Media = media;
                mp.Play();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!got && sw.ElapsedMilliseconds < timeoutMs) System.Threading.Thread.Sleep(20);
                if (!got) return null;
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try { Marshal.Copy(buf, 0, bd.Scan0, buf.Length); } finally { bmp.UnlockBits(bd); }
                return bmp;
            }
            catch { return null; }
            finally
            {
                try { if (mp != null) mp.Stop(); } catch { }
                try { if (media != null) media.Dispose(); } catch { }
                try { if (mp != null) mp.Dispose(); } catch { }   // fully stops callbacks before we free the buffer
                if (handle.IsAllocated) handle.Free();
                GC.KeepAlive(lockCb); GC.KeepAlive(unlockCb); GC.KeepAlive(displayCb);
            }
        }

        private readonly int _w, _h, _stride, _minIntervalMs;
        private readonly Control _host;
        private readonly Action<Image> _onFrame;
        private readonly object _sync = new object();

        private byte[] _buf;
        private GCHandle _bufHandle;
        private IntPtr _bufPtr;

        private MediaPlayer.LibVLCVideoLockCb _lockCb;
        private MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
        private MediaPlayer.LibVLCVideoDisplayCb _displayCb;
        private LibVLC _vlc;       // == _sharedVlc; NOT disposed here
        private MediaPlayer _mp;
        private Media _media;

        private bool _stopped;     // teardown began → OnDisplay delivers no more frames
        private int _inFlight;     // frames between Lock and Unlock — the window VLC writes _buf outside our lock
        private long _lastMs;
        private int _frames;       // frames DELIVERED to the UI (after the fps cap)
        private int _displayCalls; // ALL Display callbacks VLC fired (diagnostic: is decode continuous or one-shot?)
        private long _lastRestartMs;
        private int _loops;

        public WebmAnimator(string path, int w, int h, int fps, Control host, Action<Image> onFrame)
        {
            if (w <= 0 || w > 1024) w = 512;
            if (h <= 0 || h > 1024) h = 512;
            _w = w; _h = h; _stride = w * 4;
            _minIntervalMs = Math.Max(33, 1000 / Math.Max(1, fps));   // fps cap (>=33ms = <=30fps)
            _host = host; _onFrame = onFrame;

            _buf = new byte[_stride * _h];
            _bufHandle = GCHandle.Alloc(_buf, GCHandleType.Pinned);
            _bufPtr = _bufHandle.AddrOfPinnedObject();

            _vlc = SharedVlc();
            _mp = new MediaPlayer(_vlc);
            _lockCb = OnLock;
            _unlockCb = OnUnlock;
            _displayCb = OnDisplay;
            _mp.SetVideoFormat("RV32", (uint)_w, (uint)_h, (uint)_stride);   // RV32 = BGRX = Format32bppRgb, opaque
            _mp.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);
            _media = new Media(_vlc, path, FromType.FromPath);
            _mp.Media = _media;
            _mp.EndReached += OnEndReached;   // loop by restarting on end (vmem-safe; replaces :input-repeat)
            _mp.Play();
            System.Diagnostics.Debug.WriteLine("[WEBM] animate START " + System.IO.Path.GetFileName(path) + " " + _w + "x" + _h + " fps<=" + fps);
        }

        // VLC video thread: hand VLC the pinned buffer. _inFlight brackets the write that follows (until
        // OnUnlock) so Dispose can wait for it to finish before freeing the buffer.
        private IntPtr OnLock(IntPtr opaque, IntPtr planes)
        {
            lock (_sync) { _inFlight++; Marshal.WriteIntPtr(planes, _bufPtr); return _bufPtr; }
        }

        private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
            lock (_sync) { _inFlight--; Monitor.PulseAll(_sync); }   // write done → wake a draining Dispose
        }

        private void OnDisplay(IntPtr opaque, IntPtr picture)
        {
            long now = Environment.TickCount;
            Bitmap frame;
            lock (_sync)
            {
                _displayCalls++;
                if (_stopped) return;
                if (unchecked(now - _lastMs) < _minIntervalMs) return;   // fps cap: drop this frame
                _lastMs = now;
                frame = new Bitmap(_w, _h, PixelFormat.Format32bppRgb);
                var bd = frame.LockBits(new Rectangle(0, 0, _w, _h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try { Marshal.Copy(_buf, 0, bd.Scan0, _buf.Length); } finally { frame.UnlockBits(bd); }   // copy under the lock: mutually exclusive with the free
                _frames++;
            }
            try
            {
                if (_host != null && _host.IsHandleCreated && !_host.IsDisposed)
                    _host.BeginInvoke((Action)(() => { if (_stopped) { frame.Dispose(); return; } _onFrame(frame); }));
                else frame.Dispose();
            }
            catch { try { frame.Dispose(); } catch { } }
        }

        // VLC event thread: the clip reached its end → loop it. Player methods MUST NOT be called from the
        // event thread, so marshal the restart to the UI thread.
        private void OnEndReached(object sender, EventArgs e)
        {
            if (_stopped) return;
            try { if (_host != null && _host.IsHandleCreated && !_host.IsDisposed) _host.BeginInvoke((Action)RestartLoop); }
            catch { }
        }

        private void RestartLoop()
        {
            if (_stopped) return;
            long now = Environment.TickCount;
            if (unchecked(now - _lastRestartMs) < 250)   // anti-storm: a degenerate clip that ends instantly
            { if (TelegArm.Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[WEBM] loop restart SUPPRESSED (clip ends too fast)"); return; }
            _lastRestartMs = now;
            _loops++;
            try { _mp.Stop(); } catch { }
            try { _mp.Play(); } catch { }   // replays _media from the start — reuses the buffer, the same Media, the shared LibVLC
            if (TelegArm.Helpers.Logger.Enabled) System.Diagnostics.Debug.WriteLine("[WEBM] loop restart #" + _loops);
        }

        /// <summary>Pause/resume decoding (pause-on-background) WITHOUT tearing the player down.</summary>
        public void SetPaused(bool paused)
        {
            try { if (_mp != null && !_stopped) _mp.SetPause(paused); } catch { }
        }

        public void Dispose()
        {
            lock (_sync) { if (_stopped) return; _stopped = true; }   // OnDisplay stops delivering; RestartLoop bails

            try { if (_mp != null) _mp.EndReached -= OnEndReached; } catch { }
            try { if (_mp != null) _mp.Stop(); } catch { }   // stop the vout: no NEW Lock after this; the in-flight frame's Unlock still fires

            // Fence: wait for any in-flight VLC write (Lock..Unlock) to complete before freeing the buffer —
            // even if Stop() didn't fully join the vout. Bounded (≤5s) so a wedged player can't hang the UI.
            lock (_sync)
            {
                int spins = 0;
                while (_inFlight > 0 && spins < 200) { Monitor.Wait(_sync, 25); spins++; }
            }

            try { if (_media != null) _media.Dispose(); } catch { }
            try { if (_mp != null) _mp.Dispose(); } catch { }
            // _vlc is the shared app-lifetime instance — do NOT dispose it.
            lock (_sync) { if (_bufHandle.IsAllocated) _bufHandle.Free(); }   // safe now: vout stopped + writes drained
            GC.KeepAlive(_lockCb); GC.KeepAlive(_unlockCb); GC.KeepAlive(_displayCb);
            System.Diagnostics.Debug.WriteLine("[WEBM] animate STOP (delivered=" + _frames + " displayCalls=" + _displayCalls + " loops=" + _loops + ")");
        }
    }
}
