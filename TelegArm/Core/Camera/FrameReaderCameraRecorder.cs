using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TelegArm.Camera10;
using TelegArm.Core.Media;
using TelegArm.Helpers;
using Windows.Devices.Enumeration;

namespace TelegArm.Core.Camera
{
    /// <summary>
    /// The Windows 10 camera backend (2026-10-16, the Venetium camera handoff): a live ~30 fps preview from the camera's
    /// frame reader (TelegArm.Camera10, compiled against the Windows 10 metadata), and the recording cropped to a centred
    /// square instead of squeezed into one. The 8.1 photo-poll backend (WinRtCameraRecorder) remains for Windows 8.1, where
    /// MediaFrameReader does not exist. ONLY constructed after <see cref="CameraCapture.FrameReaderSupported"/>: this
    /// class names Windows 10 types (through TelegArm.Camera10), and 8.1 must never JIT a method that does.
    /// Preview frames are cropped to the centre square, scaled to the preview's size and mirrored (unless it is a back
    /// camera) by FFmpeg's swscale in the tgav shim, then painted latest-frame-wins.
    /// </summary>
    public sealed class FrameReaderCameraRecorder : ICameraRecorder
    {
        private const int RecordSide = 384;   // the round video's size (as the photo backend records it)

        private readonly List<CameraInfo> _cams = new List<CameraInfo>();
        private FrameReaderCamera _cam;
        private string _curId;
        private bool _mirror;
        private PreviewSurface _preview;
        private readonly VideoFrameStore _store = new VideoFrameStore();
        private readonly object _frameLock = new object();
        private IntPtr _scaler;
        private volatile int _side;             // the preview's square, in pixels (0 = no preview yet)
        private volatile bool _paintPending, _disposed;
        private string _outputPath;

        public IReadOnlyList<CameraInfo> Cameras { get { return _cams; } }

        // for the live rig (TELEGARM_LIVE=camera): frames the reader delivered, frames the preview got, what it streams
        internal int FramesDelivered { get { var c = _cam; return c != null ? c.FramesDelivered : 0; } }
        internal int PreviewFrames { get { return _store.Serial; } }
        internal string StreamDescription { get { var c = _cam; return c != null ? c.FrameWidth + "x" + c.FrameHeight + " " + c.Subtype + " @" + c.Fps.ToString("0.#") + " audio=" + c.HasAudio : "-"; } }
        public event Action<Exception> Failed;

        /// <summary>Lists the cameras and opens the front one (or the first). True when it streams frames.</summary>
        public async Task<bool> ProbeAsync()
        {
            var found = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask().ConfigureAwait(false);
            _cams.Clear();
            foreach (var d in found)
            {
                var facing = CameraFacing.Unknown;
                try
                {
                    if (d.EnclosureLocation != null)
                        facing = d.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front ? CameraFacing.Front
                               : d.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Back ? CameraFacing.Back : CameraFacing.Unknown;
                }
                catch { }
                _cams.Add(new CameraInfo { Id = d.Id, Name = d.Name, Facing = facing });
            }
            Logger.Diag("[ROUND-REC] frame reader: " + _cams.Count + " camera(s): " + string.Join(", ", _cams.Select(c => c.Name + " (" + c.Facing + ")")));
            if (_cams.Count == 0) return false;
            _scaler = Tgav.tgav_scaler_create();
            if (_scaler == IntPtr.Zero) return false;
            var prefer = _cams.FirstOrDefault(c => c.Facing == CameraFacing.Front) ?? _cams[0];
            return await OpenAsync(prefer).ConfigureAwait(false);
        }

        private async Task<bool> OpenAsync(CameraInfo info)
        {
            var cam = await FrameReaderCamera.OpenAsync(info.Id, true, 640, 480, 30, s => Logger.Diag(s)).ConfigureAwait(false);
            if (cam == null) return false;
            if (_disposed) { cam.Dispose(); return false; }
            cam.FrameArrived += OnFrame;
            cam.Failed += ex => { var h = Failed; if (h != null) h(ex); };
            _mirror = info.Facing != CameraFacing.Back;   // a selfie view (desktop webcams report Unknown and face the user too)
            _curId = info.Id;
            _cam = cam;
            return true;
        }

        public bool TryAttachPreview(Control host)
        {
            try
            {
                _preview = new PreviewSurface(this) { Dock = DockStyle.Fill };
                _preview.Resize += (s, e) => _side = Math.Min(_preview.ClientSize.Width, _preview.ClientSize.Height) & ~1;
                host.Controls.Add(_preview);
                _side = Math.Min(_preview.ClientSize.Width, _preview.ClientSize.Height) & ~1;
                return true;
            }
            catch (Exception ex) { Logger.Diag("[ROUND-REC] frame-reader preview attach failed: " + ex.Message); return false; }
        }

        /// <summary>The pump thread: the camera's centre square → the preview's size, BGRA, mirrored for a selfie view.</summary>
        private void OnFrame(ref CameraFrame f)
        {
            int side = _side;
            if (side < 2 || _disposed || _preview == null) return;
            int sq = Math.Min(f.Width, f.Height);
            lock (_frameLock)
            {
                if (_disposed) return;
                _store.EnsureBack(side, side);
                int r = Tgav.tgav_scale(_scaler, (int)f.Format, f.Plane0, f.Stride0, f.Plane1, f.Stride1, f.Width, f.Height,
                                        (f.Width - sq) / 2, (f.Height - sq) / 2, sq, sq,
                                        _store.BackPin.AddrOfPinnedObject(), side * 4, side, side, _mirror ? 1 : 0);
                if (r < 0) return;
                _store.Swap();
            }
            if (_paintPending) return;
            _paintPending = true;
            try
            {
                var p = _preview;
                if (p != null && p.IsHandleCreated && !p.IsDisposed)
                    p.BeginInvoke((Action)(() => { _paintPending = false; p.Invalidate(); }));
                else _paintPending = false;
            }
            catch { _paintPending = false; }
        }

        public async Task SelectCameraAsync(string id)
        {
            if (string.IsNullOrEmpty(id) || id == _curId) return;
            var info = _cams.FirstOrDefault(c => c.Id == id);
            if (info == null) return;
            var old = _cam;
            _cam = null;
            if (old != null) await Task.Run(() => old.Dispose()).ConfigureAwait(false);   // release it before opening the next
            if (!await OpenAsync(info).ConfigureAwait(false)) { var h = Failed; if (h != null) h(new Exception("That camera can't be opened.")); }
        }

        public async Task SwitchCameraAsync()
        {
            if (_cams.Count < 2) return;
            int i = _cams.FindIndex(c => c.Id == _curId);
            await SelectCameraAsync(_cams[(i + 1) % _cams.Count].Id).ConfigureAwait(false);
        }

        public async Task StartAsync(string outputPath)
        {
            _outputPath = outputPath;
            var cam = _cam;
            if (cam == null) throw new InvalidOperationException("No camera is open.");
            try { await cam.StartRecordingAsync(RecordSide).ConfigureAwait(false); }
            catch (Exception ex) { var h = Failed; if (h != null) h(ex); throw; }
            Logger.Diag("[ROUND-REC] frame-reader recording started → " + outputPath);
        }

        public async Task<string> StopAsync()
        {
            var cam = _cam;
            if (cam != null) await cam.StopRecordingAsync(_outputPath).ConfigureAwait(false);
            return _outputPath;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var cam = _cam;
            _cam = null;
            if (cam != null) { cam.FrameArrived -= OnFrame; cam.Dispose(); }   // releases the camera
            lock (_frameLock)
            {
                if (_scaler != IntPtr.Zero) { Tgav.tgav_scaler_free(_scaler); _scaler = IntPtr.Zero; }
                _store.Free();
            }
            Logger.Diag("[ROUND-REC] frame-reader recorder disposed (camera released)");
        }

        /// <summary>The newest preview frame, square, centred in the host (which clips it to a circle).</summary>
        private sealed class PreviewSurface : Control
        {
            private readonly FrameReaderCameraRecorder _owner;

            public PreviewSurface(FrameReaderCameraRecorder owner)
            {
                _owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
                BackColor = Color.Black;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var store = _owner._store;
                lock (store.Sync)
                {
                    if (store.Front != null && store.FrontWidth > 0)
                    {
                        var dest = DibBlit.Fit(store.FrontWidth, store.FrontHeight, ClientSize);
                        using (var b = new SolidBrush(Color.Black))
                        {
                            if (dest.Left > 0) e.Graphics.FillRectangle(b, 0, 0, dest.Left, Height);
                            if (dest.Right < Width) e.Graphics.FillRectangle(b, dest.Right, 0, Width - dest.Right, Height);
                            if (dest.Top > 0) e.Graphics.FillRectangle(b, 0, 0, Width, dest.Top);
                            if (dest.Bottom < Height) e.Graphics.FillRectangle(b, 0, dest.Bottom, Width, Height - dest.Bottom);
                        }
                        DibBlit.Draw(e.Graphics, store.Front, store.FrontWidth, store.FrontHeight, dest);
                        return;
                    }
                }
                e.Graphics.Clear(Color.Black);
            }
        }
    }
}
