using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;
using TelegArm.Core.Media;
using TelegArm.Helpers;

namespace TelegArm.UI.Controls
{
    /// <summary>
    /// Plays a short silent clip inline as a looping animation: Telegram WebM video stickers (VP9; opaque, as they were
    /// under VLC: FFmpeg's native VP9 decoder does not decode the alpha track) and GIF bubbles (MP4). 2026-10-16: FFmpeg
    /// through the tgav shim, replacing libVLC's raw-frame callbacks. One thread per animation decodes each frame at the
    /// bubble's size straight into a fresh 32 bpp bitmap and hands it to the host with BeginInvoke (the UI owns it from
    /// then on); frames are paced by their timestamps and capped at <c>fps</c>. Looping is a seek to 0 at the end.
    /// No VLC lifetime hazards remain: the decoder writes only into the bitmap the thread itself locked.
    /// </summary>
    public sealed class WebmAnimator : IDisposable
    {
        /// <summary>One frame of a video file as a bitmap (the poster of a GIF that has no thumbnail). Blocking: call it off
        /// the UI thread. Null on failure. <paramref name="timeoutMs"/> is kept for the callers (decoding one frame is quick).</summary>
        public static Bitmap GrabFirstFrame(string path, int w, int h, int timeoutMs = 4000)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            string err;
            using (var f = MediaFile.Open(path, Tgav.NoAudio | Tgav.SingleThread, out err))
            {
                if (f == null || !f.HasVideo) return null;
                if (w <= 0 || w > 1024) w = 320;
                if (h <= 0 || h > 1024) h = 240;
                return ReadBitmap(f, w, h);
            }
        }

        /// <summary>The next frame of <paramref name="f"/> as a new w x h bitmap; null at the end or on an error.</summary>
        internal static Bitmap ReadBitmap(MediaFile f, int w, int h)
        {
            int pts;
            return ReadBitmap(f, w, h, out pts);
        }

        private static Bitmap ReadBitmap(MediaFile f, int w, int h, out int pts)
        {
            pts = 0;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            int r;
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try { r = f.ReadVideo(bd.Scan0, bd.Stride, w, h, out pts); }
            finally { bmp.UnlockBits(bd); }
            if (r > 0) return bmp;
            bmp.Dispose();
            return null;
        }

        private readonly string _path;
        private readonly int _w, _h, _minIntervalMs;
        private readonly Control _host;
        private readonly Action<Image> _onFrame;
        private readonly Thread _thread;
        private readonly ManualResetEvent _running = new ManualResetEvent(true);   // reset = paused
        private volatile bool _stopped;
        private int _frames, _loops;

        public WebmAnimator(string path, int w, int h, int fps, Control host, Action<Image> onFrame)
        {
            if (w <= 0 || w > 1024) w = 512;
            if (h <= 0 || h > 1024) h = 512;
            _path = path; _w = w & ~1; _h = h & ~1;
            if (_w < 2) _w = 2;
            if (_h < 2) _h = 2;
            _minIntervalMs = Math.Max(33, 1000 / Math.Max(1, fps));   // fps cap (>= 33 ms = <= 30 fps)
            _host = host; _onFrame = onFrame;
            _thread = new Thread(Run) { IsBackground = true, Name = "webm-anim", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
            if (Logger.Enabled) Logger.Diag("[WEBM] animate START " + System.IO.Path.GetFileName(path) + " " + _w + "x" + _h + " fps<=" + fps);
        }

        private void Run()
        {
            string err;
            MediaFile f = null;
            try
            {
                f = MediaFile.Open(_path, Tgav.NoAudio | Tgav.SingleThread, out err);
                if (f == null || !f.HasVideo) { if (Logger.Enabled) Logger.Diag("[WEBM] can't open: " + err); return; }
                var clock = Stopwatch.StartNew();
                long lastShown = -_minIntervalMs;
                int startTick = Environment.TickCount;
                while (!_stopped)
                {
                    if (!_running.WaitOne(250)) { clock.Stop(); continue; }   // paused (scrolled off / app in background)
                    if (!clock.IsRunning) clock.Start();
                    int pts;
                    var bmp = ReadBitmap(f, _w, _h, out pts);
                    if (bmp == null)   // the end: loop
                    {
                        if (unchecked(Environment.TickCount - startTick) < 250) Thread.Sleep(250);   // anti-storm: a degenerate clip
                        startTick = Environment.TickCount;
                        f.Seek(0);
                        clock.Restart();
                        lastShown = -_minIntervalMs;
                        _loops++;
                        continue;
                    }
                    long wait = pts - clock.ElapsedMilliseconds;
                    if (wait > 0) Thread.Sleep((int)Math.Min(wait, 1000));
                    long now = clock.ElapsedMilliseconds;
                    if (now - lastShown < _minIntervalMs) { bmp.Dispose(); continue; }   // over the fps cap: drop this one
                    lastShown = now;
                    Deliver(bmp);
                }
            }
            catch (Exception ex) { Logger.Diag("[WEBM] thread: " + ex.Message); }
            finally
            {
                if (f != null) f.Dispose();
                if (Logger.Enabled) Logger.Diag("[WEBM] animate STOP (delivered=" + _frames + " loops=" + _loops + ")");
            }
        }

        private void Deliver(Bitmap frame)
        {
            try
            {
                if (_stopped || _host == null || !_host.IsHandleCreated || _host.IsDisposed) { frame.Dispose(); return; }
                _frames++;
                _host.BeginInvoke((Action)(() => { if (_stopped) { frame.Dispose(); return; } _onFrame(frame); }));
            }
            catch { try { frame.Dispose(); } catch { } }
        }

        /// <summary>Pause/resume decoding (pause-on-background) without tearing the decoder down.</summary>
        public void SetPaused(bool paused)
        {
            if (_stopped) return;
            if (paused) _running.Reset(); else _running.Set();
        }

        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;
            _running.Set();
            try { if (_thread.IsAlive && _thread != Thread.CurrentThread) _thread.Join(1000); } catch { }
        }
    }
}
