using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace TelegArm.Core
{
    /// <summary>
    /// Process-wide single audio player (voice/audio), with a per-chat playlist for
    /// next/previous. Audio-only LibVLC (no VideoView). One clip plays at a time.
    /// </summary>
    public static class AudioPlayer
    {
        private static LibVLC _libVlc;
        private static MediaPlayer _player;
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

        private static readonly object _ensureLock = new object();

        public static bool TryEnsure()
        {
            if (_player != null) return true;
            lock (_ensureLock)
            {
                if (_player != null) return true;
                if (!VlcEnvironment.TryInitialize()) return false;
                try
                {
                    // Lean options shave module init; the first construction also builds the shared
                    // on-disk plugin cache (the slow step on RT) — warmed up in the background at startup.
                    _libVlc = new LibVLC("--no-lua", "--no-video-title-show", "--no-stats", "--quiet");
                    _player = new MediaPlayer(_libVlc);
                    _player.Volume = _volume;
                    // Volume/mute don't stick until the audio output exists (once playing).
                    _player.Playing += (s, e) =>
                    {
                        try { _player.Volume = _volume; _player.Mute = _mute; _player.SetRate(_rate); } catch { }
                    };
                    return true;
                }
                catch { return false; }
            }
        }

        public static bool IsActive => CurrentId != 0;
        public static bool IsPlaying => _player != null && _player.IsPlaying;
        public static bool IsPlayingId(long id) => CurrentId == id && IsPlaying;
        public static float Position => _player != null ? _player.Position : 0f;
        public static long TimeMs => _player != null ? _player.Time : 0;
        public static long DurationMs => _player != null ? _player.Length : 0;
        public static VLCState State => _player != null ? _player.State : VLCState.NothingSpecial;
        public static bool HasNext => _index >= 0 && _index + 1 < _list.Count;
        public static bool HasPrev => _index > 0;
        public static float Rate => _rate;

        public static int Volume
        {
            get { return _volume; }
            set { _volume = value; if (_player != null) _player.Volume = value; Raise(); }
        }

        public static bool Mute
        {
            get { return _mute; }
            set { _mute = value; if (_player != null) _player.Mute = value; Raise(); }
        }

        public static void SetRate(float rate) { _rate = rate; if (_player != null) _player.SetRate(rate); Raise(); }

        public static void Seek(double ratio)
        {
            if (_player == null) return;
            float p = (float)ratio;
            if (p < 0) p = 0; else if (p > 1) p = 1;
            _player.Position = p;
        }

        public static void SetPlaylist(List<(long id, string title)> items, Func<long, Task<string>> resolve)
        {
            _list = items ?? new List<(long, string)>();
            _resolve = resolve;
            _index = _list.FindIndex(x => x.id == CurrentId);
        }

        /// <summary>Pause/resume if it's the current clip, otherwise start it. Returns false if VLC unavailable.</summary>
        public static bool Toggle(long id, string path, string title)
        {
            if (!TryEnsure()) return false;
            if (CurrentId == id) { TogglePause(); return true; }
            PlayNew(id, path, title);
            return true;
        }

        public static void TogglePause()
        {
            if (_player == null) return;
            if (_player.IsPlaying) _player.Pause(); else _player.Play();
            Raise();
        }

        private static void PlayNew(long id, string path, string title)
        {
            try { _player.Stop(); } catch { }
            CurrentId = id;
            Title = title;
            _rate = 1.0f;
            _index = _list.FindIndex(x => x.id == id);
            using (var media = new Media(_libVlc, new Uri(path)))
                _player.Play(media);
            _player.Volume = _volume;
            _player.Mute = _mute;
            _player.SetRate(_rate);
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
            if (string.IsNullOrEmpty(path) || _player == null) return;
            PlayNew(item.id, path, item.title);
        }

        public static void Stop()
        {
            try { _player?.Stop(); } catch { }
            CurrentId = 0;
            Title = null;
            Raise();
        }

        public static void Shutdown()
        {
            try { _player?.Stop(); } catch { }
            try { _player?.Dispose(); } catch { }
            try { _libVlc?.Dispose(); } catch { }
            _player = null; _libVlc = null; CurrentId = 0; Title = null;
        }
    }
}
