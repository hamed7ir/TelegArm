using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TelegArm.Core.Media;

namespace TelegArm.Core
{
    /// <summary>
    /// Process-wide single audio player (voice notes, music), with a per-chat playlist for next/previous. One clip plays
    /// at a time. 2026-10-16: FFmpeg (Core/Media, the tgav shim) instead of an audio-only libVLC; speed changes keep the
    /// pitch (atempo). The UI polls <see cref="State"/> (MiniPlayerBar advances on Ended), as it did with VLC.
    /// </summary>
    public static class AudioPlayer
    {
        private static MediaPlayback _pb;
        private static int _volume = 100;
        private static bool _mute;
        private static float _rate = 1.0f;

        private static List<(long id, string title)> _list = new List<(long, string)>();
        private static Func<long, Task<string>> _resolve;
        private static int _index = -1;

        public static long CurrentId { get; private set; }
        public static string Title { get; private set; }

        /// <summary>Raised on play/pause/stop/track change (invoked on the calling thread).</summary>
        public static event Action StateChanged;
        private static void Raise() { StateChanged?.Invoke(); }

        /// <summary>Loads the media engine (once). False when it isn't there.</summary>
        public static bool TryEnsure() { return Tgav.Available; }

        public static bool IsActive => CurrentId != 0;
        public static bool IsPlaying => _pb != null && _pb.State == MediaState.Playing;
        public static bool IsPlayingId(long id) => CurrentId == id && IsPlaying;
        public static float Position => _pb != null ? _pb.Position : 0f;
        public static long TimeMs => _pb != null ? _pb.TimeMs : 0;
        public static long DurationMs => _pb != null ? _pb.LengthMs : 0;
        public static MediaState State => _pb != null ? _pb.State : MediaState.NothingSpecial;
        public static bool HasNext => _index >= 0 && _index + 1 < _list.Count;
        public static bool HasPrev => _index > 0;
        public static float Rate => _rate;

        public static int Volume
        {
            get { return _volume; }
            set { _volume = value; if (_pb != null) _pb.Volume = value; Raise(); }
        }

        public static bool Mute
        {
            get { return _mute; }
            set { _mute = value; if (_pb != null) _pb.Mute = value; Raise(); }
        }

        public static void SetRate(float rate)
        {
            _rate = rate;
            if (_pb != null) _pb.TempoPercent = (int)Math.Round(rate * 100);
            Raise();
        }

        public static void Seek(double ratio)
        {
            if (_pb == null) return;
            _pb.Position = (float)Math.Max(0, Math.Min(1, ratio));
        }

        public static void SetPlaylist(List<(long id, string title)> items, Func<long, Task<string>> resolve)
        {
            _list = items ?? new List<(long, string)>();
            _resolve = resolve;
            _index = _list.FindIndex(x => x.id == CurrentId);
        }

        /// <summary>Pause/resume if it's the current clip, otherwise start it. False if the media engine is unavailable.</summary>
        public static bool Toggle(long id, string path, string title)
        {
            if (!TryEnsure()) return false;
            if (CurrentId == id && _pb != null && _pb.State != MediaState.Error) { TogglePause(); return true; }
            PlayNew(id, path, title);
            return true;
        }

        public static void TogglePause()
        {
            if (_pb == null) return;
            _pb.TogglePause();
            Raise();
        }

        private static void PlayNew(long id, string path, string title)
        {
            DisposePlayback();
            CurrentId = id;
            Title = title;
            _rate = 1.0f;
            _index = _list.FindIndex(x => x.id == id);
            _pb = new MediaPlayback(path, loop: false, video: false, audio: true) { Volume = _volume, Mute = _mute };
            Raise();
        }

        public static async void Next() { await Step(+1); }
        public static async void Previous() { await Step(-1); }

        private static async Task Step(int dir)
        {
            if (_list.Count == 0 || _resolve == null) return;
            int idx = (_index >= 0 ? _index : _list.FindIndex(x => x.id == CurrentId)) + dir;
            if (idx < 0 || idx >= _list.Count) return;
            var item = _list[idx];
            string path = await _resolve(item.id);
            if (string.IsNullOrEmpty(path)) return;
            PlayNew(item.id, path, item.title);
        }

        public static void Stop()
        {
            DisposePlayback();
            CurrentId = 0;
            Title = null;
            Raise();
        }

        public static void Shutdown()
        {
            DisposePlayback();
            CurrentId = 0; Title = null;
        }

        private static void DisposePlayback()
        {
            var pb = _pb;
            _pb = null;
            if (pb != null) try { pb.Dispose(); } catch { }
        }
    }
}
