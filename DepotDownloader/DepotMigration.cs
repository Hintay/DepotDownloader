// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Collections.Generic;

namespace DepotDownloader
{
    sealed class MigrationCandidate(uint depotId, ulong manifestId, string sourceDir, string targetDir)
    {
        public uint DepotId { get; } = depotId;
        public ulong ManifestId { get; } = manifestId;
        public string SourceDir { get; } = sourceDir;
        public string TargetDir { get; } = targetDir;
    }

    static class DepotMigration
    {
        // Subdirectory name used by depot-mode for per-depot state (mirrors ContentDownloader.CONFIG_DIR).
        internal const string CONFIG_DIR_NAME = ".DepotDownloader";

        // Default depots-mode library root (mirrors ContentDownloader.DEFAULT_DOWNLOAD_DIR).
        internal const string DEPOTS_ROOT = "depots";

        public static IReadOnlyList<MigrationCandidate> Detect(
            IEnumerable<(uint depotId, ulong manifestId)> requested,
            string depotsRoot,
            string targetDir)
        {
            return new List<MigrationCandidate>();
        }

        public static IReadOnlyList<MigrationCandidate> Prompt(IReadOnlyList<MigrationCandidate> candidates)
        {
            return candidates;
        }

        public static void Apply(MigrationCandidate candidate)
        {
        }

        public static void MaybeMigrate(
            IEnumerable<(uint depotId, ulong manifestId)> requested,
            string targetDir,
            bool autoMigrate,
            bool interactive)
        {
        }
    }
}
