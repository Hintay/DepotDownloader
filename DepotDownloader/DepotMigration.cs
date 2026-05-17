// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Collections.Generic;
using System.IO;
using Spectre.Console;

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
        // the global singleton. Mirrors DepotConfigStore.LoadFromFile's deflate+protobuf
        // shape; protobuf-net invokes DepotConfigStore's private parameterless ctor
        // directly, so no reflection is needed. If the depot-mode depot.config format
        // changes, both this method and DepotConfigStore.LoadFromFile must be updated
        // in lockstep.
        static Dictionary<uint, ulong> LoadDepotModeManifestIds(string path)
        {
            using var fs = File.OpenRead(path);
            using var ds = new System.IO.Compression.DeflateStream(fs, System.IO.Compression.CompressionMode.Decompress);
            var store = ProtoBuf.Serializer.Deserialize<DepotConfigStore>(ds);
            return store.InstalledManifestIDs ?? new Dictionary<uint, ulong>();
        }

        public static IReadOnlyList<MigrationCandidate> Prompt(IReadOnlyList<MigrationCandidate> candidates)
        {
            if (candidates.Count == 0)
            {
                return candidates;
            }

            System.Console.WriteLine("Found {0} depot-mode install(s) that can be migrated to {1}:",
                candidates.Count, candidates[0].TargetDir);
            foreach (var c in candidates)
            {
                System.Console.WriteLine("  - depot {0}  ->  {1}", c.DepotId, c.SourceDir);
            }
            System.Console.Write("Migrate now? [Y]es / [n]o / [p]er-depot: ");

            var input = (System.Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();

            switch (input)
            {
                case "":
                case "y":
                case "yes":
                    return candidates;

                case "p":
                case "per":
                case "per-depot":
                    return PromptPerDepot(candidates);

                default:
                    return new List<MigrationCandidate>();
            }
        }

        static IReadOnlyList<MigrationCandidate> PromptPerDepot(IReadOnlyList<MigrationCandidate> candidates)
        {
            var prompt = new MultiSelectionPrompt<MigrationCandidate>()
                .Title("Select depots to migrate:")
                .NotRequired()
                .PageSize(15)
                .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to confirm)[/]")
                .UseConverter(c => $"depot {c.DepotId}  ->  {c.SourceDir}");

            foreach (var c in candidates)
            {
                prompt.AddChoice(c).Select();
            }

            var selected = AnsiConsole.Prompt(prompt);
            return selected;
        }

        public static void Apply(MigrationCandidate candidate)
        {
            // Move every direct child of SourceDir into TargetDir, except the .DepotDownloader/ subdirectory.
            Directory.CreateDirectory(candidate.TargetDir);
            foreach (var entry in Directory.GetFileSystemEntries(candidate.SourceDir))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, CONFIG_DIR_NAME, System.StringComparison.Ordinal))
                {
                    continue;
                }

                var target = Path.Combine(candidate.TargetDir, name);
                if (Directory.Exists(entry))
                {
                    MergeMoveDirectory(entry, target);
                }
                else
                {
                    File.Move(entry, target, overwrite: true);
                }
            }

            // Move the manifest binary from source/.DepotDownloader/ to the app-mode .DepotDownloader/.
            // App-mode .DepotDownloader/ lives at TargetDir's grandparent + CONFIG_DIR_NAME — but the
            // existing app-mode convention puts depot.config at <configPath>/.DepotDownloader/, where
            // <configPath> in Steam-layout is "steamapps". We derive the app-mode config dir from
            // DepotConfigStore.Instance.FileName (set when LoadFromFile was called by the caller).
            var appConfigDir = Path.GetDirectoryName(GetDepotConfigStoreFileName());
            if (!string.IsNullOrEmpty(appConfigDir))
            {
                Directory.CreateDirectory(appConfigDir);
                var sourceManifest = Path.Combine(candidate.SourceDir, CONFIG_DIR_NAME, $"{candidate.DepotId}_{candidate.ManifestId}.manifest");
                var targetManifest = Path.Combine(appConfigDir, $"{candidate.DepotId}_{candidate.ManifestId}.manifest");
                if (File.Exists(sourceManifest))
                {
                    File.Move(sourceManifest, targetManifest, overwrite: true);
                }
            }

            // Update app-mode DepotConfigStore.Instance.
            DepotConfigStore.Instance.InstalledManifestIDs[candidate.DepotId] = candidate.ManifestId;
            DepotConfigStore.Save();

            // Delete the now-empty source directory.
            Directory.Delete(candidate.SourceDir, recursive: true);

            // If ./depots/<depotId>/ is now empty (no other build-id subdirs), delete it too.
            var depotParent = Path.GetDirectoryName(candidate.SourceDir);
            if (!string.IsNullOrEmpty(depotParent) && Directory.Exists(depotParent))
            {
                if (Directory.GetFileSystemEntries(depotParent).Length == 0)
                {
                    Directory.Delete(depotParent);
                }
            }
        }

        // Recursive merge-move: moves source's children into target, creating target subdirs
        // as needed, overwriting target files on conflict.
        static void MergeMoveDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var entry in Directory.GetFileSystemEntries(source))
            {
                var name = Path.GetFileName(entry);
                var dest = Path.Combine(target, name);
                if (Directory.Exists(entry))
                {
                    MergeMoveDirectory(entry, dest);
                }
                else
                {
                    File.Move(entry, dest, overwrite: true);
                }
            }
            Directory.Delete(source, recursive: false);  // source is now empty (children moved)
        }

        // DepotConfigStore.FileName is a private string field; we read it via reflection because
        // the public surface doesn't expose it. The caller (DownloadAppAsync) is expected to have
        // called DepotConfigStore.LoadFromFile() with the app-mode path, so this returns that.
        static string GetDepotConfigStoreFileName()
        {
            var field = typeof(DepotConfigStore).GetField("FileName",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (string)field.GetValue(DepotConfigStore.Instance);
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
