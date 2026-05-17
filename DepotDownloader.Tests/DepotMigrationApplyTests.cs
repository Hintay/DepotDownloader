// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using DepotDownloader;
using Xunit;

namespace DepotDownloader.Tests
{
    // DepotConfigStore is a process-global singleton; tests touching it must run serialized
    // to avoid "Config already loaded" races when xUnit parallelizes different test classes.
    [Collection("DepotConfigStoreSingleton")]
    public class DepotMigrationApplyTests : IDisposable
    {
        readonly string scratch;

        public DepotMigrationApplyTests()
        {
            scratch = Path.Combine(Path.GetTempPath(), $"dd-migrate-apply-{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratch);
        }

        public void Dispose()
        {
            ResetDepotConfigStoreSingleton();
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }

        [Fact]
        public void Apply_MovesAllFilesAndManifestBinary_DeletesSource_UpdatesStore()
        {
            // Source: depots/601151/16243/{game.exe, data/asset.bin, .DepotDownloader/601151_5034.manifest}
            var depotsRoot = Path.Combine(scratch, "depots");
            var sourceDir = Path.Combine(depotsRoot, "601151", "16243");
            Directory.CreateDirectory(Path.Combine(sourceDir, "data"));
            Directory.CreateDirectory(Path.Combine(sourceDir, ".DepotDownloader"));
            File.WriteAllText(Path.Combine(sourceDir, "game.exe"), "exe content");
            File.WriteAllText(Path.Combine(sourceDir, "data", "asset.bin"), "asset bytes");
            File.WriteAllText(Path.Combine(sourceDir, ".DepotDownloader", "601151_5034.manifest"), "manifest binary");

            // Target: steamapps/common/Game (empty, will be created during Apply)
            var targetDir = Path.Combine(scratch, "steamapps", "common", "Game");
            Directory.CreateDirectory(targetDir);

            // App-mode DepotConfigStore needs to be loaded before Apply mutates it.
            var appConfigPath = Path.Combine(scratch, "steamapps", DepotMigration.CONFIG_DIR_NAME, "depot.config");
            Directory.CreateDirectory(Path.GetDirectoryName(appConfigPath));
            DepotConfigStore.LoadFromFile(appConfigPath);

            var candidate = new MigrationCandidate(601151u, 5034UL, sourceDir, targetDir);
            DepotMigration.Apply(candidate);

            // Target has the moved files
            Assert.True(File.Exists(Path.Combine(targetDir, "game.exe")));
            Assert.Equal("exe content", File.ReadAllText(Path.Combine(targetDir, "game.exe")));
            Assert.True(File.Exists(Path.Combine(targetDir, "data", "asset.bin")));
            Assert.Equal("asset bytes", File.ReadAllText(Path.Combine(targetDir, "data", "asset.bin")));

            // App-mode .DepotDownloader/ has the manifest binary
            var appManifestPath = Path.Combine(scratch, "steamapps", DepotMigration.CONFIG_DIR_NAME, "601151_5034.manifest");
            Assert.True(File.Exists(appManifestPath));
            Assert.Equal("manifest binary", File.ReadAllText(appManifestPath));

            // Source dir gone
            Assert.False(Directory.Exists(sourceDir));

            // DepotConfigStore.Instance reflects the new state
            Assert.True(DepotConfigStore.Instance.InstalledManifestIDs.ContainsKey(601151u));
            Assert.Equal(5034UL, DepotConfigStore.Instance.InstalledManifestIDs[601151u]);
        }

        [Fact]
        public void Apply_OverwritesExistingTargetFiles()
        {
            // Target already has a file (e.g., partial app-mode install or earlier migration).
            // Apply must overwrite, not fail.
            var depotsRoot = Path.Combine(scratch, "depots");
            var sourceDir = Path.Combine(depotsRoot, "601151", "16243");
            Directory.CreateDirectory(Path.Combine(sourceDir, ".DepotDownloader"));
            File.WriteAllText(Path.Combine(sourceDir, "game.exe"), "new content");
            File.WriteAllText(Path.Combine(sourceDir, ".DepotDownloader", "601151_5034.manifest"), "m");

            var targetDir = Path.Combine(scratch, "steamapps", "common", "Game");
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, "game.exe"), "stale content");

            var appConfigPath = Path.Combine(scratch, "steamapps", DepotMigration.CONFIG_DIR_NAME, "depot.config");
            Directory.CreateDirectory(Path.GetDirectoryName(appConfigPath));
            DepotConfigStore.LoadFromFile(appConfigPath);

            var candidate = new MigrationCandidate(601151u, 5034UL, sourceDir, targetDir);
            DepotMigration.Apply(candidate);

            Assert.Equal("new content", File.ReadAllText(Path.Combine(targetDir, "game.exe")));
        }

        [Fact]
        public void Apply_DeletesEmptyDepotParentDirectory()
        {
            // After Apply removes ./depots/601151/16243/, if ./depots/601151/ has no other
            // build-id subdirectories, it should be removed too.
            var depotsRoot = Path.Combine(scratch, "depots");
            var sourceDir = Path.Combine(depotsRoot, "601151", "16243");
            Directory.CreateDirectory(Path.Combine(sourceDir, ".DepotDownloader"));
            File.WriteAllText(Path.Combine(sourceDir, ".DepotDownloader", "601151_5034.manifest"), "m");

            var targetDir = Path.Combine(scratch, "steamapps", "common", "Game");
            Directory.CreateDirectory(targetDir);

            var appConfigPath = Path.Combine(scratch, "steamapps", DepotMigration.CONFIG_DIR_NAME, "depot.config");
            Directory.CreateDirectory(Path.GetDirectoryName(appConfigPath));
            DepotConfigStore.LoadFromFile(appConfigPath);

            var candidate = new MigrationCandidate(601151u, 5034UL, sourceDir, targetDir);
            DepotMigration.Apply(candidate);

            Assert.False(Directory.Exists(Path.Combine(depotsRoot, "601151")));
        }

        [Fact]
        public void Apply_KeepsDepotParentDirectoryIfOtherBuildIdSubdirsRemain()
        {
            // ./depots/601151/16243/ (being migrated) AND ./depots/601151/16500/ (untouched).
            // The 601151/ parent must remain.
            var depotsRoot = Path.Combine(scratch, "depots");
            var sourceDir = Path.Combine(depotsRoot, "601151", "16243");
            Directory.CreateDirectory(Path.Combine(sourceDir, ".DepotDownloader"));
            File.WriteAllText(Path.Combine(sourceDir, ".DepotDownloader", "601151_5034.manifest"), "m");

            var siblingDir = Path.Combine(depotsRoot, "601151", "16500");
            Directory.CreateDirectory(siblingDir);
            File.WriteAllText(Path.Combine(siblingDir, "marker"), "do not touch");

            var targetDir = Path.Combine(scratch, "steamapps", "common", "Game");
            Directory.CreateDirectory(targetDir);

            var appConfigPath = Path.Combine(scratch, "steamapps", DepotMigration.CONFIG_DIR_NAME, "depot.config");
            Directory.CreateDirectory(Path.GetDirectoryName(appConfigPath));
            DepotConfigStore.LoadFromFile(appConfigPath);

            var candidate = new MigrationCandidate(601151u, 5034UL, sourceDir, targetDir);
            DepotMigration.Apply(candidate);

            Assert.True(Directory.Exists(Path.Combine(depotsRoot, "601151")));
            Assert.True(Directory.Exists(siblingDir));
            Assert.True(File.Exists(Path.Combine(siblingDir, "marker")));
        }

        static void ResetDepotConfigStoreSingleton()
        {
            typeof(DepotConfigStore)
                .GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .SetValue(null, null);
        }
    }
}
