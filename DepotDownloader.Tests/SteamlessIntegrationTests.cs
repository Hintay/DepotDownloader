// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
        public void ReplaceOriginalWithPatched_PatchedMoveFails_RestoresOriginal()
        {
            var gameDir = Path.Combine(scratch, "replace-rollback");
            Directory.CreateDirectory(gameDir);
            var original = Path.Combine(gameDir, "Game.exe");
            var missingPatched = Path.Combine(gameDir, "Game.unpacked.exe");
            File.WriteAllText(original, "original");

            Assert.Throws<FileNotFoundException>(() => SteamlessIntegration.ReplaceOriginalWithPatched(original, missingPatched));

            Assert.Equal("original", File.ReadAllText(original));
            Assert.False(File.Exists(Path.Combine(gameDir, "Game.original.exe")));
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

        [Fact]
        public async Task TryRunAsync_MissingSteamless_ReturnsSkippedSummary()
        {
            var gameDir = Path.Combine(scratch, "missing-steamless-game");
            Directory.CreateDirectory(gameDir);
            WriteSizedFile(Path.Combine(gameDir, "Game.exe"), 200 * 1024);
            var logs = new List<string>();

            var summary = await SteamlessIntegration.TryRunAsync(
                gameDir,
                runner: new FakeSteamlessRunner((target, log) => new SteamlessProcessResult(0, "unused")),
                logLine: logs.Add,
                baseDirectory: Path.Combine(scratch, "base"),
                getEnvironmentVariable: _ => null);

            Assert.False(summary.SteamlessFound);
            Assert.Equal(0, summary.CandidateCount);
            Assert.Empty(summary.Results);
            Assert.Contains(logs, line => line.Contains("Steamless not found", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task TryRunAsync_PatchedOutput_ReplacesOriginalAndSummarizesPatched()
        {
            var gameDir = Path.Combine(scratch, "patched-game");
            var steamlessDir = Path.Combine(scratch, "steamless");
            Directory.CreateDirectory(gameDir);
            CreateSteamlessDirectory(steamlessDir);
            var exe = Path.Combine(gameDir, "Game.exe");
            WriteSizedFile(exe, 200 * 1024);

            var runner = new FakeSteamlessRunner((target, log) =>
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(target), "Game.unpacked.exe"), "patched");
                log("runner output");
                return new SteamlessProcessResult(0, "Successfully unpacked file!");
            });
            var logs = new List<string>();

            var summary = await SteamlessIntegration.TryRunAsync(
                gameDir,
                runner,
                logs.Add,
                baseDirectory: scratch,
                getEnvironmentVariable: name => name == SteamlessIntegration.STEAMLESS_PATH_ENVIRONMENT_VARIABLE ? steamlessDir : null);

            var result = Assert.Single(summary.Results);
            Assert.True(summary.SteamlessFound);
            Assert.Equal(SteamlessPatchStatus.Patched, result.Status);
            Assert.True(File.Exists(Path.Combine(gameDir, "Game.original.exe")));
            Assert.Equal("patched", File.ReadAllText(exe));
            Assert.Contains(logs, line => line.Contains("runner output", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(logs, line => line.Contains("patched: 1", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task TryRunAsync_NoDrmOutput_DoesNotReplaceOriginal()
        {
            var gameDir = Path.Combine(scratch, "nodrm-game");
            var steamlessDir = Path.Combine(scratch, "steamless-nodrm");
            Directory.CreateDirectory(gameDir);
            CreateSteamlessDirectory(steamlessDir);
            var exe = Path.Combine(gameDir, "Game.exe");
            WriteSizedFile(exe, 200 * 1024);
            var originalBytes = File.ReadAllBytes(exe);

            var summary = await SteamlessIntegration.TryRunAsync(
                gameDir,
                runner: new FakeSteamlessRunner((target, log) => new SteamlessProcessResult(1, "All unpackers failed to unpack file.")),
                logLine: _ => { },
                baseDirectory: scratch,
                getEnvironmentVariable: name => name == SteamlessIntegration.STEAMLESS_PATH_ENVIRONMENT_VARIABLE ? steamlessDir : null);

            var result = Assert.Single(summary.Results);
            Assert.Equal(SteamlessPatchStatus.NoDrmDetected, result.Status);
            Assert.Equal(originalBytes, File.ReadAllBytes(exe));
            Assert.False(File.Exists(Path.Combine(gameDir, "Game.original.exe")));
        }

        [Fact]
        public async Task TryRunAsync_UnexpectedRunnerError_ReturnsFailedSummary()
        {
            var gameDir = Path.Combine(scratch, "runner-error-game");
            var steamlessDir = Path.Combine(scratch, "steamless-runner-error");
            Directory.CreateDirectory(gameDir);
            CreateSteamlessDirectory(steamlessDir);
            WriteSizedFile(Path.Combine(gameDir, "Game.exe"), 200 * 1024);
            var logs = new List<string>();

            var summary = await SteamlessIntegration.TryRunAsync(
                gameDir,
                runner: new FakeSteamlessRunner((target, log) => throw new NotSupportedException("boom")),
                logLine: logs.Add,
                baseDirectory: scratch,
                getEnvironmentVariable: name => name == SteamlessIntegration.STEAMLESS_PATH_ENVIRONMENT_VARIABLE ? steamlessDir : null);

            var result = Assert.Single(summary.Results);
            Assert.Equal(SteamlessPatchStatus.Failed, result.Status);
            Assert.Contains("boom", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(logs, line => line.Contains("failed: 1", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task TryRunAsync_UnexpectedDiscoveryError_ReturnsFailedSummary()
        {
            var logs = new List<string>();

            var summary = await SteamlessIntegration.TryRunAsync(
                Path.Combine(scratch, "game"),
                runner: new FakeSteamlessRunner((target, log) => new SteamlessProcessResult(0, "unused")),
                logLine: logs.Add,
                getEnvironmentVariable: _ => throw new InvalidOperationException("discovery failed"));

            Assert.False(summary.SteamlessFound);
            Assert.Equal(0, summary.CandidateCount);
            var result = Assert.Single(summary.Results);
            Assert.Equal(SteamlessPatchStatus.Failed, result.Status);
            Assert.Contains("discovery failed", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(logs, line => line.Contains("Steamless warning", StringComparison.OrdinalIgnoreCase));
        }

        sealed class FakeSteamlessRunner : ISteamlessProcessRunner
        {
            readonly Func<string, Action<string>, SteamlessProcessResult> run;

            public FakeSteamlessRunner(Func<string, Action<string>, SteamlessProcessResult> run)
            {
                this.run = run;
            }

            public Task<SteamlessProcessResult> RunAsync(
                string steamlessExe,
                string steamlessDirectory,
                string targetExe,
                Action<string> logLine,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(run(targetExe, logLine));
            }
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
