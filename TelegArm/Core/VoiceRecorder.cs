using System;
using System.Collections.Generic;
using System.IO;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;

namespace TelegArm.Core
{
    /// <summary>
    /// Records the microphone to an OGG/Opus file (a real Telegram voice note), using NAudio
    /// for capture and Concentus for encoding — both pure-managed, so this runs on ARM32 with
    /// no native binaries. Capture is mono 48 kHz; frames are streamed straight to disk via the
    /// OGG writer (never buffered whole in memory).
    /// </summary>
    public sealed class VoiceRecorder : IDisposable
    {
        private const int SampleRate = 48000;
        private const int PeakWindow = 480;   // 10 ms @ 48 kHz → one amplitude sample per window

        private WaveInEvent _waveIn;
        private FileStream _file;
        private OpusOggWriteStream _ogg;
        private string _path;
        private long _samples;
        private bool _recording;
        private bool _cancel;
        private bool _finished;

        // Amplitude peaks accumulated from the PCM during capture (for the Telegram waveform).
        private readonly List<int> _peaks = new List<int>();
        private int _peakMax, _peakCount;

        public bool IsRecording => _recording;
        public int ElapsedSeconds => (int)(_samples / SampleRate);

        /// <summary>The 5-bit-packed amplitude waveform (Telegram bars); set once finalized.</summary>
        public byte[] Waveform { get; private set; }

        /// <summary>
        /// Raised (on a background thread) when the file is finalized. <c>path</c> is the OGG path,
        /// or null if cancelled / too short / failed. <c>seconds</c> is the (rounded) duration;
        /// <c>waveform</c> is the 5-bit-packed amplitude bars.
        /// </summary>
        public event Action<string, int, byte[]> Finished;

        public void Start()
        {
            if (_recording) return;
            _cancel = false;
            _finished = false;
            _samples = 0;
            _peaks.Clear();
            _peakMax = 0; _peakCount = 0;
            Waveform = null;

            _path = Path.Combine(Path.GetTempPath(), "tgvoice_" + Guid.NewGuid().ToString("N") + ".ogg");
            _file = File.Create(_path);

            var encoder = OpusEncoder.Create(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 24000;
            _ogg = new OpusOggWriteStream(encoder, _file);

            _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 60 };
            _waveIn.DataAvailable += OnData;
            _waveIn.RecordingStopped += OnStopped;

            _recording = true;
            _waveIn.StartRecording();   // throws if there's no microphone / format unsupported
        }

        private void OnData(object sender, WaveInEventArgs e)
        {
            if (!_recording || e.BytesRecorded <= 0) return;
            int n = e.BytesRecorded / 2;
            var buf = new short[n];
            Buffer.BlockCopy(e.Buffer, 0, buf, 0, e.BytesRecorded);

            // Accumulate amplitude peaks (one per 10 ms) for the waveform — reads PCM only, never altered.
            for (int i = 0; i < n; i++)
            {
                int a = buf[i]; if (a < 0) a = -a;
                if (a > _peakMax) _peakMax = a;
                if (++_peakCount >= PeakWindow) { _peaks.Add(_peakMax); _peakMax = 0; _peakCount = 0; }
            }

            try { _ogg.WriteSamples(buf, 0, n); _samples += n; } catch { }
        }

        public void Stop() => StopInternal(false);
        public void Cancel() => StopInternal(true);

        private void StopInternal(bool cancel)
        {
            if (!_recording) return;
            _cancel = cancel;
            _recording = false;
            try { _waveIn.StopRecording(); }   // → OnStopped finalizes
            catch { OnStopped(null, null); }
        }

        private void OnStopped(object sender, StoppedEventArgs e)
        {
            if (_finished) return;
            _finished = true;

            if (_peakCount > 0) { _peaks.Add(_peakMax); _peakMax = 0; _peakCount = 0; }   // flush partial window
            int seconds = (int)Math.Round((double)_samples / SampleRate);                  // 1.76s → 2 (not 1)
            Waveform = BuildWaveform();

            try { _ogg?.Finish(); } catch { }
            try { _file?.Dispose(); } catch { }
            try { _waveIn?.Dispose(); } catch { }
            _ogg = null; _file = null; _waveIn = null;

            string result = _path;
            if (_cancel || _samples < SampleRate / 2)   // cancelled or under ~0.5s → discard
            {
                try { if (_path != null && File.Exists(_path)) File.Delete(_path); } catch { }
                result = null;
            }

            // Fix the OpusHead pre_skip (0 → 312) so Telegram Desktop plays it. CRC-guarded: if our
            // checksum doesn't verify against the file's stored CRC, the file is left byte-for-byte intact.
            if (result != null) { try { PatchOpusPreSkip(result, 312); } catch { } }

            // Drop Concentus' empty EOS page (the RFC-violating zero-length packet that Desktop rejects).
            // Same CRC-guard pattern; runs after the pre_skip patch, on kept files only.
            if (result != null) { try { RemuxDropEmptyEos(result); } catch { } }

            Finished?.Invoke(result, seconds, Waveform);
        }

        /// <summary>Resamples the accumulated peaks to 100 buckets, normalizes 0–31, packs 5-bit (Telegram layout).</summary>
        private byte[] BuildWaveform()
        {
            const int N = 100;
            var values = new int[N];
            if (_peaks.Count > 0)
            {
                int max = 1;
                foreach (var p in _peaks) if (p > max) max = p;
                for (int i = 0; i < N; i++)
                {
                    int start = (int)((long)i * _peaks.Count / N);
                    int end = (int)((long)(i + 1) * _peaks.Count / N);
                    if (end <= start) end = start + 1;
                    int pk = 0;
                    for (int j = start; j < end && j < _peaks.Count; j++) if (_peaks[j] > pk) pk = _peaks[j];
                    values[i] = Math.Min(31, (int)Math.Round((double)pk / max * 31.0));
                }
            }
            return Pack5Bit(values);
        }

        /// <summary>Packs unsigned 5-bit values LSB-first (matches tdesktop's documentWaveformEncode5bit).</summary>
        private static byte[] Pack5Bit(int[] values)
        {
            var bytes = new byte[(values.Length * 5 + 7) / 8];
            int bit = 0;
            foreach (int v in values)
            {
                int val = v & 0x1F;
                int idx = bit >> 3, shift = bit & 7;
                bytes[idx] |= (byte)(val << shift);
                if (shift > 3 && idx + 1 < bytes.Length) bytes[idx + 1] |= (byte)(val >> (8 - shift));
                bit += 5;
            }
            return bytes;
        }

        // ── OpusHead pre_skip patch (OGG page-0 only, CRC-guarded) ───────────
        /// <summary>
        /// Sets the OpusHead pre_skip (bytes +10/+11 LE) on OGG page 0 and recomputes that page's
        /// CRC. SAFETY: only applies the patch if our CRC of the UNMODIFIED page matches the file's
        /// stored CRC — otherwise the file is left byte-for-byte untouched (so it can never corrupt
        /// the OGG / regress the iOS path). Touches only pre_skip + the page-0 CRC.
        /// </summary>
        private static void PatchOpusPreSkip(string path, int preSkip)
        {
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 28 || d[0] != (byte)'O' || d[1] != (byte)'g' || d[2] != (byte)'g' || d[3] != (byte)'S') return;

            int nseg = d[26];
            int headerLen = 27 + nseg;
            if (d.Length < headerLen) return;
            int dataLen = 0;
            for (int i = 0; i < nseg; i++) dataLen += d[27 + i];
            int pageLen = headerLen + dataLen;
            if (d.Length < pageLen) return;

            int oh = IndexOf(d, "OpusHead", headerLen, pageLen);   // OpusHead must live in page 0's data
            if (oh < 0) return;
            int ps = oh + 10;                                       // magic(8)+version(1)+channels(1)
            if (ps + 1 >= pageLen) return;

            // SAFETY GUARD: verify our CRC matches the file's stored CRC before changing anything.
            uint stored = (uint)(d[22] | (d[23] << 8) | (d[24] << 16) | (d[25] << 24));
            if (OggPageCrc(d, 0, pageLen) != stored) return;        // unverified → leave file intact

            if (d[ps] == (byte)(preSkip & 0xFF) && d[ps + 1] == (byte)((preSkip >> 8) & 0xFF)) return;  // already set
            d[ps] = (byte)(preSkip & 0xFF);
            d[ps + 1] = (byte)((preSkip >> 8) & 0xFF);

            uint newCrc = OggPageCrc(d, 0, pageLen);
            d[22] = (byte)newCrc; d[23] = (byte)(newCrc >> 8); d[24] = (byte)(newCrc >> 16); d[25] = (byte)(newCrc >> 24);
            File.WriteAllBytes(path, d);
        }

        // ── Re-mux: drop the empty EOS page Concentus writes (the trailing zero-length Opus packet) ──
        /// <summary>
        /// Concentus' OpusOggWriteStream.Finish() ends the stream with an empty EOS page (one 0-length
        /// lacing value) → a zero-length Opus packet, which violates RFC 6716 §3.1. Telegram Desktop's
        /// demuxer rejects it (silent); phones tolerate it. This drops that empty page and moves the EOS
        /// flag onto the last DATA page (recomputing its CRC), so the stream terminates exactly like a
        /// real Telegram note. CRC-guarded: if our CRC of the unmodified target page doesn't match its
        /// stored CRC, the file is left byte-for-byte untouched. Audio packets/OpusHead are unchanged.
        /// </summary>
        private static void RemuxDropEmptyEos(string path)
        {
            byte[] d = File.ReadAllBytes(path);

            // Walk pages by lacing (jumping page-to-page avoids matching "OggS" inside packet data).
            var offs = new List<int>();
            var totals = new List<int>();
            int i = 0;
            while (true)
            {
                int j = IndexOf(d, "OggS", i, d.Length);
                if (j < 0 || j + 27 > d.Length) break;
                int nseg = d[j + 26];
                if (j + 27 + nseg > d.Length) break;
                int dlen = 0;
                for (int s = 0; s < nseg; s++) dlen += d[j + 27 + s];
                int total = 27 + nseg + dlen;
                if (j + total > d.Length) break;
                offs.Add(j); totals.Add(total);
                i = j + total;
            }
            if (offs.Count < 2) return;

            // Find the last EOS page.
            int eosIdx = -1;
            for (int k = 0; k < offs.Count; k++) if ((d[offs[k] + 5] & 0x04) != 0) eosIdx = k;
            if (eosIdx <= 0) return;                                  // no EOS, or it's the first page

            int eosOff = offs[eosIdx], eosTotal = totals[eosIdx];
            int eosNseg = d[eosOff + 26];
            int eosDlen = 0;
            for (int s = 0; s < eosNseg; s++) eosDlen += d[eosOff + 27 + s];
            if (eosDlen != 0) return;                                 // EOS already carries data → nothing to fix

            int tOff = offs[eosIdx - 1], tTotal = totals[eosIdx - 1];

            // SAFETY GUARD: our CRC of the unmodified target page must match its stored CRC.
            uint stored = (uint)(d[tOff + 22] | (d[tOff + 23] << 8) | (d[tOff + 24] << 16) | (d[tOff + 25] << 24));
            if (OggPageCrc(d, tOff, tTotal) != stored) return;       // unverified → leave file intact

            d[tOff + 5] |= 0x04;                                      // move EOS flag onto the last data page
            uint nc = OggPageCrc(d, tOff, tTotal);
            d[tOff + 22] = (byte)nc; d[tOff + 23] = (byte)(nc >> 8); d[tOff + 24] = (byte)(nc >> 16); d[tOff + 25] = (byte)(nc >> 24);

            // Drop the empty EOS page; preserve any trailing bytes after it (there should be none).
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                fs.Write(d, 0, eosOff);
                int tail = eosOff + eosTotal;
                if (tail < d.Length) fs.Write(d, tail, d.Length - tail);
            }
        }

        private static int IndexOf(byte[] d, string ascii, int start, int end)
        {
            for (int i = start; i <= end - ascii.Length; i++)
            {
                bool m = true;
                for (int j = 0; j < ascii.Length; j++) if (d[i + j] != (byte)ascii[j]) { m = false; break; }
                if (m) return i;
            }
            return -1;
        }

        // OGG checksum: CRC-32/MPEG-2 (poly 0x04C11DB7, init 0, no reflection, no final XOR),
        // computed over the whole page with the 4-byte CRC field (offset 22) treated as zero.
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : (c << 1);
                t[i] = c;
            }
            return t;
        }

        private static uint OggPageCrc(byte[] data, int start, int pageLen)
        {
            uint crc = 0;
            for (int i = 0; i < pageLen; i++)
            {
                byte b = (i >= 22 && i <= 25) ? (byte)0 : data[start + i];   // zero the CRC field while hashing
                crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ b) & 0xFF];
            }
            return crc;
        }

        public void Dispose()
        {
            try { if (_recording) { _cancel = true; _recording = false; _waveIn?.StopRecording(); } } catch { }
            try { if (!_finished) { _ogg?.Finish(); _file?.Dispose(); _waveIn?.Dispose(); } } catch { }
        }
    }
}
