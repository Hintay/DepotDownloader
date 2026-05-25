// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.IO;
using DepotDownloader;
using Xunit;

namespace DepotDownloader.Tests
{
    public sealed class SteamlessIntegrationTests : IDisposable
    {
        readonly string scratch;

        public SteamlessIntegrationTests()
        {
            scratch = Path.Combine(Path.GetTempPath(), $"dd-steamless-{Guid.NewGuid():N}");
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
        public void ResolveSteamlessDirectory_ValidEnvironmentPath_Wins()
        {
            var baseDir = Path.Combine(scratch, "base");
            var envDir = Path.Combine(scratch, "env-steamless");
            CreateSteamlessDirectory(envDir);
            CreateSteamlessDirectory(Path.Combine(baseDir, "Steamless"));

            var resolved = SteamlessIntegration.ResolveSteamlessDirectory(
                baseDir,
                name => name == SteamlessIntegration.STEAMLESS_PATH_ENVIRONMENT_VARIABLE ? envDir : null);

            Assert.Equal(envDir, resolved.DirectoryPath);
            Assert.Equal(Path.Combine(envDir, "Steamless.CLI.exe"), resolved.ExecutablePath);
        }

        [Fact]
        public void ResolveSteamlessDirectory_InvalidEnvironmentPath_FallsBackToBaseDirectory()
        {
            var baseDir = Path.Combine(scratch, "base");
            var localDir = Path.Combine(baseDir, "Steamless");
            CreateSteamlessDirectory(localDir);

            var resolved = SteamlessIntegration.ResolveSteamlessDirectory(
                baseDir,
                name => name == SteamlessIntegration.STEAMLESS_PATH_ENVIRONMENT_VARIABLE
                    ? Path.Combine(scratch, "missing")
                    : null);

            Assert.Equal(localDir, resolved.DirectoryPath);
        }

        [Fact]
        public void ResolveSteamlessDirectory_MissingCliOrPlugins_ReturnsNull()
        {
            var baseDir = Path.Combine(scratch, "base");
            Directory.CreateDirectory(Path.Combine(baseDir, "Steamless"));

            var resolved = SteamlessIntegration.ResolveSteamlessDirectory(baseDir, _ => null);

            Assert.Null(resolved);
        }

        [Fact]
        public void FindGameExecutables_FiltersGeneratedInstallersCrashHandlersAndSmallFiles()
        {
            var gameDir = Path.Combine(scratch, "Example Game");
            Directory.CreateDirectory(gameDir);
            WriteSizedFile(Path.Combine(gameDir, "Example Game.exe"), 200 * 1024);
            WriteSizedFile(Path.Combine(gameDir, "setup.exe"), 200 * 1024);
            WriteSizedFile(Path.Combine(gameDir, "UnityCrashHandler64.exe"), 200 * 1024);
            WriteSizedFile(Path.Combine(gameDir, "Game.original.exe"), 200 * 1024);
            WriteSizedFile(Path.Combine(gameDir, "Tiny.exe"), 20 * 1024);

            var result = SteamlessIntegration.FindGameExecutables(gameDir).ConvertAll(Path.GetFileName);

            var exe = Assert.Single(result);
            Assert.Equal("Example Game.exe", exe);
        }

        [Fact]
        public void FindGameExecutables_SortsMainExecutableBeforeTools()
        {
            var gameDir = Path.Combine(scratch, "Cool Game");
            Directory.CreateDirectory(gameDir);
            WriteSizedFile(Path.Combine(gameDir, "tool.exe"), 900 * 1024);
            WriteSizedFile(Path.Combine(gameDir, "Cool Game.exe"), 200 * 1024);
            WriteSizedFile(Path.Combine(gameDir, "game.exe"), 300 * 1024);

            var result = SteamlessIntegration.FindGameExecutables(gameDir).ConvertAll(Path.GetFileName);

            Assert.Equal("Cool Game.exe", result[0]);
            Assert.Contains("game.exe", result);
            Assert.Contains("tool.exe", result);
        }

        [Theory]
        [InlineData("Game.exe.unpacked.exe")]
        [InlineData("Game.unpacked.exe")]
        [InlineData("Game.exe.steamless.exe")]
        [InlineData("Game.steamless.exe")]
        public void ResolvePatchedOutputPath_FindsSupportedOutputNames(string outputName)
        {
            var gameDir = Path.Combine(scratch, "output");
            Directory.CreateDirectory(gameDir);
            var original = Path.Combine(gameDir, "Game.exe");
            var output = Path.Combine(gameDir, outputName);
            File.WriteAllText(original, "original");
            File.WriteAllText(output, "patched");

            var resolved = SteamlessIntegration.ResolvePatchedOutputPath(original);

            Assert.Equal(output, resolved);
        }

        [Fact]
        public void ReplaceOriginalWithPatched_CreatesBackupAndMovesPatchedIntoOriginalPath()
        {
            var gameDir = Path.Combine(scratch, "replace");
            Directory.CreateDirectory(gameDir);
            var original = Path.Combine(gameDir, "Game.exe");
            var patched = Path.Combine(gameDir, "Game.unpacked.exe");
            File.WriteAllText(original, "original");
            File.WriteAllText(patched, "patched");

            var backup = SteamlessIntegration.ReplaceOriginalWithPatched(original, patched);

            Assert.Equal(Path.Combine(gameDir, "Game.original.exe"), backup);
            Assert.Equal("original", File.ReadAllText(backup));
            Assert.Equal("patched", File.ReadAllText(original));
            Assert.False(File.Exists(patched));
        }

        [Fact]
        public void ReplaceOriginalWithPatched_ExistingBackupCreatesNumberedBackup()
        {
            var gameDir = Path.Combine(scratch, "replace-numbered");
            Directory.CreateDirectory(gameDir);
            var original = Path.Combine(gameDir, "Game.exe");
            var firstBackup = Path.Combine(gameDir, "Game.original.exe");
            var patched = Path.Combine(gameDir, "Game.unpacked.exe");
            File.WriteAllText(original, "original2");
            File.WriteAllText(firstBackup, "original1");
            File.WriteAllText(patched, "patched");

            var backup = SteamlessIntegration.ReplaceOriginalWithPatched(original, patched);

            Assert.Equal(Path.Combine(gameDir, "Game.original.1.exe"), backup);
            Assert.Equal("original1", File.ReadAllText(firstBackup));
            Assert.Equal("original2", File.ReadAllText(backup));
            Assert.Equal("patched", File.ReadAllText(original));
        }

        [Fact]
        public void ClassifyProcessResult_ExitCodeZeroWithOutputPath_ReturnsPatched()
        {
            var status = SteamlessIntegration.ClassifyProcessResult(
                new SteamlessProcessResult(0, "Successfully unpacked file!"),
                patchedOutputPath: Path.Combine(scratch, "Game.unpacked.exe"));

            Assert.Equal(SteamlessPatchStatus.Patched, status);
        }

        [Fact]
        public void ClassifyProcessResult_NoDrmLikeOutput_ReturnsNoDrmDetected()
        {
            var status = SteamlessIntegration.ClassifyProcessResult(
                new SteamlessProcessResult(1, "All unpackers failed to unpack file."),
                patchedOutputPath: null);

            Assert.Equal(SteamlessPatchStatus.NoDrmDetected, status);
        }

        [Fact]
        public void ClassifyProcessResult_OtherNonZeroExit_ReturnsFailed()
        {
            var status = SteamlessIntegration.ClassifyProcessResult(
                new SteamlessProcessResult(2, "No plugins were loaded; be sure to fully extract Steamless before running!"),
                patchedOutputPath: null);

            Assert.Equal(SteamlessPatchStatus.Failed, status);
        }

        static void CreateSteamlessDirectory(string path)
        {
            Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(path, "Plugins"));
            File.WriteAllText(Path.Combine(path, "Steamless.CLI.exe"), "fake exe");
        }

        static void WriteSizedFile(string path, int length)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[length]);
        }
    }
}
