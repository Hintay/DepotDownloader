// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Collections.Generic;
using System.IO;

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
            var result = new List<MigrationCandidate>();

            if (string.IsNullOrWhiteSpace(depotsRoot) || !Directory.Exists(depotsRoot))
            {
                return result;
            }

            var depotConfigPath = Path.Combine(depotsRoot, CONFIG_DIR_NAME, "depot.config");
            if (!File.Exists(depotConfigPath))
            {
                return result;
            }

            // Load depot-mode store *temporarily* into a local instance separate from
            // the app-mode singleton. We don't touch the global DepotConfigStore.Instance
            // here — the caller's app-mode store stays intact.
            Dictionary<uint, ulong> depotModeManifests;
            try
            {
                depotModeManifests = LoadDepotModeManifestIds(depotConfigPath);
            }
            catch
            {
                return result;
            }

            foreach (var (depotId, manifestId) in requested)
            {
                if (!depotModeManifests.TryGetValue(depotId, out var installedManifestId))
                {
                    continue;
                }
                if (installedManifestId != manifestId)
                {
                    continue;
                }

                var depotRoot = Path.Combine(depotsRoot, depotId.ToString());
                if (!Directory.Exists(depotRoot))
                {
                    continue;
                }

                // Scan ./depots/<depotId>/*/ for the matching manifest binary.
                // Sort by directory name (ordinal) for deterministic selection in the
                // (practically impossible) multi-match case.
                var subdirs = Directory.GetDirectories(depotRoot);
                System.Array.Sort(subdirs, System.StringComparer.Ordinal);

                string sourceDir = null;
                var manifestBinaryName = $"{depotId}_{manifestId}.manifest";
                foreach (var subdir in subdirs)
                {
                    var manifestPath = Path.Combine(subdir, CONFIG_DIR_NAME, manifestBinaryName);
                    if (File.Exists(manifestPath))
                    {
                        sourceDir = subdir;
                        break;
                    }
                }

                if (sourceDir == null)
                {
                    continue;
                }

                result.Add(new MigrationCandidate(depotId, manifestId, sourceDir, targetDir));
            }

            return result;
        }

        // Reads a compressed-protobuf DepotConfigStore from disk without touching
        // the global singleton. We use the same deflate+protobuf shape as
        // DepotConfigStore.LoadFromFile but instantiate locally via reflection
        // (DepotConfigStore's ctor and Instance setter are private).
        static Dictionary<uint, ulong> LoadDepotModeManifestIds(string path)
        {
            using var fs = File.OpenRead(path);
            using var ds = new System.IO.Compression.DeflateStream(fs, System.IO.Compression.CompressionMode.Decompress);
            var store = ProtoBuf.Serializer.Deserialize<DepotConfigStore>(ds);
            return store.InstalledManifestIDs ?? new Dictionary<uint, ulong>();
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
