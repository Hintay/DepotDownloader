// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DepotDownloader
{
    internal enum SteamlessPatchStatus
    {
        Patched,
        NoDrmDetected,
        Failed,
        Skipped,
    }

    internal sealed record SteamlessLocation(string DirectoryPath, string ExecutablePath);

    internal sealed record SteamlessProcessResult(int ExitCode, string Output);

    internal sealed record SteamlessPatchResult(
        string ExecutablePath,
        SteamlessPatchStatus Status,
        string Message);

    internal sealed record SteamlessSummary(
        bool SteamlessFound,
        string SteamlessDirectory,
        int CandidateCount,
        IReadOnlyList<SteamlessPatchResult> Results);

    internal interface ISteamlessProcessRunner
    {
        Task<SteamlessProcessResult> RunAsync(
            string steamlessExe,
            string steamlessDirectory,
            string targetExe,
            Action<string> logLine,
            CancellationToken cancellationToken = default);
    }

    internal static class SteamlessIntegration
    {
        public const string STEAMLESS_PATH_ENVIRONMENT_VARIABLE = "STEAMLESS_PATH";
        const long MINIMUM_EXECUTABLE_SIZE_BYTES = 100 * 1024;

        static readonly string[] IgnoredExactPrefixes =
        [
            "unins",
            "setup",
            "install",
            "config",
            "launcher",
            "updater",
            "patch",
            "redist",
            "vcredist",
            "dxsetup",
            "physx",
            "unity",
        ];

        static readonly string[] IgnoredContains =
        [
            "crash",
            "handler",
            "debug",
            "unitycrash",
        ];

        static readonly string[] LowPriorityWords =
        [
            "editor",
            "tool",
            "settings",
            "config",
            "debug",
        ];

        public static SteamlessLocation ResolveSteamlessDirectory(
            string baseDirectory = null,
            Func<string, string> getEnvironmentVariable = null)
        {
            getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
            baseDirectory ??= AppContext.BaseDirectory;

            var envPath = getEnvironmentVariable(STEAMLESS_PATH_ENVIRONMENT_VARIABLE);
            if (!string.IsNullOrWhiteSpace(envPath) && TryCreateLocation(envPath, out var envLocation))
            {
                return envLocation;
            }

            var localPath = Path.Combine(baseDirectory, "Steamless");
            return TryCreateLocation(localPath, out var localLocation) ? localLocation : null;
        }

        static bool TryCreateLocation(string directory, out SteamlessLocation location)
        {
            location = null;
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            var steamlessExe = Path.Combine(directory, "Steamless.CLI.exe");
            var pluginsDir = Path.Combine(directory, "Plugins");
            if (!File.Exists(steamlessExe) || !Directory.Exists(pluginsDir))
            {
                return false;
            }

            location = new SteamlessLocation(directory, steamlessExe);
            return true;
        }

        public static List<string> FindGameExecutables(string gameDirectory)
        {
            if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            {
                return [];
            }

            var gameName = new DirectoryInfo(gameDirectory).Name;
            return Directory.EnumerateFiles(gameDirectory, "*.exe", SearchOption.AllDirectories)
                .Where(IsCandidateExecutable)
                .OrderByDescending(path => GetExecutablePriority(path, gameName))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static bool IsCandidateExecutable(string path)
        {
            var fileName = Path.GetFileName(path);
            var lower = fileName.ToLowerInvariant();
            if (lower.EndsWith(".original.exe", StringComparison.Ordinal)
                || lower.EndsWith(".unpacked.exe", StringComparison.Ordinal)
                || lower.EndsWith(".steamless.exe", StringComparison.Ordinal))
            {
                return false;
            }

            var nameWithoutExtension = Path.GetFileNameWithoutExtension(lower);
            if (IgnoredExactPrefixes.Any(prefix => nameWithoutExtension.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return false;
            }

            if (IgnoredContains.Any(word => nameWithoutExtension.Contains(word, StringComparison.Ordinal)))
            {
                return false;
            }

            try
            {
                return new FileInfo(path).Length >= MINIMUM_EXECUTABLE_SIZE_BYTES;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        static long GetExecutablePriority(string path, string gameName)
        {
            var file = new FileInfo(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var lowerName = name.ToLowerInvariant();
            var normalizedName = NormalizeName(name);
            var normalizedGameName = NormalizeName(gameName);
            long score = 0;

            if (!string.IsNullOrWhiteSpace(normalizedGameName) && normalizedName == normalizedGameName)
            {
                score += 1_000_000;
            }

            if (lowerName is "game" or "main" or "play" or "start")
            {
                score += 500_000;
            }

            if (LowPriorityWords.Any(word => lowerName.Contains(word, StringComparison.Ordinal)))
            {
                score -= 250_000;
            }

            score += Math.Min(file.Length / 1024, 100_000);
            return score;
        }

        static string NormalizeName(string value)
        {
            return new string((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        public static string ResolvePatchedOutputPath(string originalExe)
        {
            var directory = Path.GetDirectoryName(originalExe);
            var fileName = Path.GetFileName(originalExe);
            var withoutExtension = Path.GetFileNameWithoutExtension(originalExe);
            var candidates = new[]
            {
                Path.Combine(directory, fileName + ".unpacked.exe"),
                Path.Combine(directory, withoutExtension + ".unpacked.exe"),
                Path.Combine(directory, fileName + ".steamless.exe"),
                Path.Combine(directory, withoutExtension + ".steamless.exe"),
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        public static string ReplaceOriginalWithPatched(string originalExe, string patchedOutput)
        {
            var backupPath = GetAvailableBackupPath(originalExe);
            File.Move(originalExe, backupPath);
            File.Move(patchedOutput, originalExe);
            return backupPath;
        }

        static string GetAvailableBackupPath(string originalExe)
        {
            var directory = Path.GetDirectoryName(originalExe);
            var withoutExtension = Path.GetFileNameWithoutExtension(originalExe);
            var backup = Path.Combine(directory, withoutExtension + ".original.exe");
            if (!File.Exists(backup))
            {
                return backup;
            }

            for (var index = 1; ; index++)
            {
                var numbered = Path.Combine(directory, withoutExtension + $".original.{index}.exe");
                if (!File.Exists(numbered))
                {
                    return numbered;
                }
            }
        }

        public static SteamlessPatchStatus ClassifyProcessResult(SteamlessProcessResult processResult, string patchedOutputPath)
        {
            if (processResult.ExitCode == 0)
            {
                return string.IsNullOrWhiteSpace(patchedOutputPath)
                    ? SteamlessPatchStatus.Failed
                    : SteamlessPatchStatus.Patched;
            }

            return LooksLikeNoDrm(processResult.Output)
                ? SteamlessPatchStatus.NoDrmDetected
                : SteamlessPatchStatus.Failed;
        }

        static bool LooksLikeNoDrm(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            return output.Contains("All unpackers failed to unpack file", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Failed to unpack file", StringComparison.OrdinalIgnoreCase);
        }
    }
}
