using System;
using System.Collections.Generic;

namespace TelegArm.SelfTest
{
    /// <summary>The settings TelegArm 1.9.0 persists — a FROZEN copy of its AppSettings' public properties
    /// (git show 79ae90a:TelegArm/Core/AppSettings.cs). It stands in for the old build: deserializing this build's
    /// settings.json into it with Json.NET's defaults is exactly what 1.9.0 does on a rollback (unknown fields
    /// ignored), so "nothing lost" can be checked without running 1.9.0.
    /// ⚠ NEVER EDIT IT to match a newer AppSettings — that would make the rollback check prove nothing.</summary>
    internal sealed class V19Settings
    {
        public bool AutoDownloadPhotos { get; set; }
        public bool AutoDownloadVideos { get; set; }
        public bool AutoDownloadDocuments { get; set; }
        public bool AutoDownloadVoice { get; set; }
        public bool AutoDownloadAudio { get; set; }
        public bool AutoDownloadGifs { get; set; }
        public bool AutoPlayGifs { get; set; }
        public int MaxAutoDownloadSizeMB { get; set; }
        public bool AnimateWebmStickers { get; set; }
        public bool EnableNotifications { get; set; }
        public string SendKey { get; set; }
        public bool NotificationPreviews { get; set; }
        public bool LegacyTrayBalloon { get; set; }
        public bool FileLogging { get; set; }
        public bool ResizeForKeyboard { get; set; }
        public bool KbdDiag { get; set; }
        public bool CompositedScroll { get; set; }
        public bool FolderSidebar { get; set; }
        public bool DpiUnaware { get; set; }
        public bool WarmConnections { get; set; }
        public bool ProxyEnabled { get; set; }
        public List<string> ProxyList { get; set; }
        public int ProxyActive { get; set; }
        public string ThemeMode { get; set; }
        public string MediaCacheFolder { get; set; }
        public string DefaultSaveFolder { get; set; }
        public int MediaCacheRetentionDays { get; set; }
        public DateTime LastCacheCleanup { get; set; }
    }
}
