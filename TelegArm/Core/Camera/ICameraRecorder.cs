using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TelegArm.Core.Camera
{
    public enum CameraFacing { Unknown, Front, Back }

    /// <summary>A camera the recorder can use (backend-neutral).</summary>
    public sealed class CameraInfo
    {
        public string Id;
        public string Name;
        public CameraFacing Facing;
    }

    /// <summary>
    /// Backend-agnostic round-video recorder. NO WinRT/VLC types cross this boundary — the UI talks only to
    /// this, and the backend (WinRT MediaCapture by default, libVLC DirectShow as the Win7 fallback) is chosen
    /// by <see cref="CameraCapture.CreateAsync"/>. Both backends produce the IDENTICAL 384×384 H.264+AAC MP4.
    /// </summary>
    public interface ICameraRecorder : IDisposable
    {
        IReadOnlyList<CameraInfo> Cameras { get; }
        /// <summary>Attaches a live preview into <paramref name="host"/>; returns true if one was attached
        /// (false → the UI shows a recording indicator instead).</summary>
        bool TryAttachPreview(Control host);
        Task SelectCameraAsync(string id);
        /// <summary>Begins recording to a 384×384 MP4 at <paramref name="outputPath"/>.</summary>
        Task StartAsync(string outputPath);
        /// <summary>Stops + FINALIZES the MP4 (moov written) and returns the path.</summary>
        Task<string> StopAsync();
        Task SwitchCameraAsync();
        event Action<Exception> Failed;
    }
}
