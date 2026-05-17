// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using DepotDownloader;
using ProtoBuf;
using Xunit;

namespace DepotDownloader.Tests
{
    public class DepotMigrationDetectTests : IDisposable
    {
        readonly string scratch;

        public DepotMigrationDetectTests()
        {
            scratch = Path.Combine(Path.GetTempPath(), $"dd-migrate-detect-{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratch);
        }

        public void Dispose()
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }

        [Fact]
        public void Detect_NoDepotsRoot_ReturnsEmpty()
        {
            var depotsRoot = Path.Combine(scratch, "depots");
            // depots/ does not exist
            Assert.False(Directory.Exists(depotsRoot));

            var result = DepotMigration.Detect(
                new[] { (depotId: 601151u, manifestId: 5034UL) },
                depotsRoot,
                Path.Combine(scratch, "steamapps", "common", "Game"));

            Assert.Empty(result);
        }

        [Fact]
        public void Detect_NoConfigFile_ReturnsEmpty()
        {
            var depotsRoot = Path.Combine(scratch, "depots");
            Directory.CreateDirectory(depotsRoot);
            // No depot.config under .DepotDownloader

            var result = DepotMigration.Detect(
                new[] { (depotId: 601151u, manifestId: 5034UL) },
                depotsRoot,
                Path.Combine(scratch, "steamapps", "common", "Game"));

            Assert.Empty(result);
        }

        [Fact]
        public void Detect_ExactManifestMatch_ReturnsCandidate()
        {
            var depotsRoot = Path.Combine(scratch, "depots");
            WriteDepotModeConfig(depotsRoot, new Dictionary<uint, ulong> { [601151u] = 5034UL });
            var sourceDir = Path.Combine(depotsRoot, "601151", "16243");
            Directory.CreateDirectory(Path.Combine(sourceDir, ".DepotDownloader"));
            File.WriteAllText(Path.Combine(sourceDir, ".DepotDownloader", "601151_5034.manifest"), "fake manifest binary");

            var result = DepotMigration.Detect(
                new[] { (depotId: 601151u, manifestId: 5034UL) },
                depotsRoot,
                Path.Combine(scratch, "steamapps", "common", "Game"));

            var candidate = Assert.Single(result);
            Assert.Equal(601151u, candidate.DepotId);
            Assert.Equal(5034UL, candidate.ManifestId);
            Assert.Equal(sourceDir, candidate.SourceDir);
            Assert.Equal(Path.Combine(scratch, "steamapps", "common", "Game"), candidate.TargetDir);
        }

        [Fact]
        public void Detect_ManifestMismatchInConfig_SkipsDepot()
        {
            var depotsRoot = Path.Combine(scratch, "depots");
            // Config says manifest 9999, but caller requested 5034
            WriteDepotModeConfig(depotsRoot, new Dictionary<uint, ulong> { [601151u] = 9999UL });
            var sourceDir = Path.Combine(depotsRoot, "601151", "16243");
            Directory.CreateDirectory(Path.Combine(sourceDir, ".DepotDownloader"));
            File.WriteAllText(Path.Combine(sourceDir, ".DepotDownloader", "601151_9999.manifest"), "fake manifest binary");

            var result = DepotMigration.Detect(
                new[] { (depotId: 601151u, manifestId: 5034UL) },
                depotsRoot,
                Path.Combine(scratch, "steamapps", "common", "Game"));

            Assert.Empty(result);
        }

        [Fact]
        public void Detect_InvalidManifestIdInConfig_SkipsDepot()
        {
            var depotsRoot = Path.Combine(scratch, "depots");
            WriteDepotModeConfig(depotsRoot, new Dictionary<uint, ulong> { [601151u] = ulong.MaxValue });
            var sourceDir = Path.Combine(depotsRoot, "601151", "16243");
            Directory.CreateDirectory(Path.Combine(sourceDir, ".DepotDownloader"));

            var result = DepotMigration.Detect(
                new[] { (depotId: 601151u, manifestId: 5034UL) },
                depotsRoot,
                Path.Combine(scratch, "steamapps", "common", "Game"));

            Assert.Empty(result);
        }

        [Fact]
        public void Detect_ConfigMatchesButManifestBinaryMissing_SkipsDepot()
        {
            var depotsRoot = Path.Combine(scratch, "depots");
            WriteDepotModeConfig(depotsRoot, new Dictionary<uint, ulong> { [601151u] = 5034UL });
            // Source dir exists but no manifest binary inside .DepotDownloader/
            var sourceDir = Path.Combine(depotsRoot, "601151", "16243");
            Directory.CreateDirectory(Path.Combine(sourceDir, ".DepotDownloader"));

            var result = DepotMigration.Detect(
                new[] { (depotId: 601151u, manifestId: 5034UL) },
                depotsRoot,
                Path.Combine(scratch, "steamapps", "common", "Game"));

            Assert.Empty(result);
        }

        [Fact]
        public void Detect_DepotNotInRequest_SkipsEvenIfInstalled()
        {
            var depotsRoot = Path.Combine(scratch, "depots");
            WriteDepotModeConfig(depotsRoot, new Dictionary<uint, ulong> { [228987u] = 4302UL });
            var sourceDir = Path.Combine(depotsRoot, "228987", "16243");
            Directory.CreateDirectory(Path.Combine(sourceDir, ".DepotDownloader"));
            File.WriteAllText(Path.Combine(sourceDir, ".DepotDownloader", "228987_4302.manifest"), "fake");

            var result = DepotMigration.Detect(
                new[] { (depotId: 601151u, manifestId: 5034UL) },  // requesting 601151, not 228987
                depotsRoot,
                Path.Combine(scratch, "steamapps", "common", "Game"));

            Assert.Empty(result);
        }

        // Writes a depot-mode depot.config (compressed protobuf, same format as DepotConfigStore.Save)
        // at <depotsRoot>/.DepotDownloader/depot.config with the given InstalledManifestIDs.
        static void WriteDepotModeConfig(string depotsRoot, Dictionary<uint, ulong> installedManifestIds)
        {
            var configDir = Path.Combine(depotsRoot, DepotMigration.CONFIG_DIR_NAME);
            Directory.CreateDirectory(configDir);
            var configPath = Path.Combine(configDir, "depot.config");

            DepotConfigStore.LoadFromFile(configPath);
            try
            {
                foreach (var kv in installedManifestIds)
                {
                    DepotConfigStore.Instance.InstalledManifestIDs[kv.Key] = kv.Value;
                }
                DepotConfigStore.Save();
            }
            finally
            {
                // Reset the singleton so other tests don't observe leaked state from this fixture.
                ResetDepotConfigStoreSingleton();
            }
        }

        // The DepotConfigStore singleton is process-global; tests must clean up to avoid cross-test pollution.
        static void ResetDepotConfigStoreSingleton()
        {
            typeof(DepotConfigStore)
                .GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .SetValue(null, null);
        }
    }
}
