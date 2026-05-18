// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DepotDownloader
{
    class DownloadConfig
    {
        public int CellID { get; set; }
        public bool DownloadAllPlatforms { get; set; }
        public bool DownloadAllArchs { get; set; }
        public bool DownloadAllLanguages { get; set; }
        public bool DownloadManifestOnly { get; set; }
        public string InstallDirectory { get; set; }

        public bool UsingFileList { get; set; }
        public HashSet<string> FilesToDownload { get; set; }
        public List<Regex> FilesToDownloadRegex { get; set; }

        public string BetaPassword { get; set; }

        public bool VerifyAll { get; set; }

        public int MaxDownloads { get; set; }

        public bool RememberPassword { get; set; }

        // A Steam LoginID to allow multiple concurrent connections
        public uint? LoginID { get; set; }

        public bool UseQrCode { get; set; }
        public bool SkipAppConfirmation { get; set; }

        public bool UseManifestFile { get; set; }
        public string ManifestFile { get; set; }
        public bool UseManifestDirectory { get; set; }
        public string ManifestDirectory { get; set; }
        public bool GenerateAppManifest { get; set; }
        public bool JsonProgress { get; set; }
        public string AppManifestFile { get; set; }
        public bool UseLuaFile { get; set; }
        public string LuaFile { get; set; }
        public Dictionary<uint, ulong> LuaManifestIds { get; set; }
        public Dictionary<uint, ulong> LuaAppTokens { get; set; }
        public bool BatchLuaDownload { get; set; }
        public bool HasExplicitDepots { get; set; }
        public bool MigrateDepotInstalls { get; set; }
        public HashSet<uint> LuaOwnedApps { get; set; }
        public bool HasExplicitPlatformArgs { get; set; }
    }
}
