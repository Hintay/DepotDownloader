// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using Spectre.Console;
using SteamKit2;

namespace DepotDownloader
{
    sealed class PlatformSelection
    {
        public bool AllPlatforms { get; init; }
        public string Os { get; init; }
        public bool AllArchs { get; init; }
        public string Arch { get; init; }
        public bool AllLanguages { get; init; }
        public string Language { get; init; }
    }

    static class AppSelectionPrompt
    {
        // Pure helper — tested. Walks the main app's PICS depots tree and collects
        // distinct values of config.oslist / config.osarch / config.language.
        public static (
            IReadOnlyList<string> Os,
            IReadOnlyList<string> Arch,
            IReadOnlyList<string> Language)
        ExtractPlatformChoices(KeyValue mainAppDepots)
        {
            return ExtractPlatformChoices(mainAppDepots, null);
        }

        public static (
            IReadOnlyList<string> Os,
            IReadOnlyList<string> Arch,
            IReadOnlyList<string> Language)
        ExtractPlatformChoices(KeyValue mainAppDepots, KeyValue mainAppCommon)
        {
            var osSet = new SortedSet<string>(StringComparer.Ordinal);
            var archSet = new SortedSet<string>(StringComparer.Ordinal);
            var languageSet = new SortedSet<string>(StringComparer.Ordinal);

            if (mainAppDepots == null || mainAppDepots == KeyValue.Invalid)
            {
                return (new List<string>(osSet), new List<string>(archSet), new List<string>(languageSet));
            }

            foreach (var depotSection in mainAppDepots.Children)
            {
                var cfg = depotSection["config"];
                if (cfg == KeyValue.Invalid)
                {
                    continue;
                }

                var oslist = cfg["oslist"];
                if (oslist != KeyValue.Invalid && !string.IsNullOrWhiteSpace(oslist.Value))
                {
                    foreach (var part in oslist.Value.Split(','))
                    {
                        var trimmed = part.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            osSet.Add(trimmed);
                        }
                    }
                }

                var osarch = cfg["osarch"];
                if (osarch != KeyValue.Invalid && !string.IsNullOrWhiteSpace(osarch.Value))
                {
                    archSet.Add(osarch.Value.Trim());
                }

                var language = cfg["language"];
                if (language != KeyValue.Invalid && !string.IsNullOrWhiteSpace(language.Value))
                {
                    languageSet.Add(language.Value.Trim());
                }
            }

            return (new List<string>(osSet), new List<string>(archSet), new List<string>(languageSet));
        }

        // Pure helper — tested. Given the main app's PICS depots tree, the appId,
        // and the user's platform selection, returns the list of "main depot" IDs
        // that would download under those constraints (excluding shared depots
        // pointing to other apps and depots whose platform metadata excludes them).
        public static List<uint> ComputeMainDepotCandidates(
            KeyValue mainAppDepots,
            uint appId,
            string os,
            bool allPlatforms,
            string arch,
            bool allArchs,
            string language,
            bool allLanguages)
        {
            var result = new List<uint>();
            if (mainAppDepots == null || mainAppDepots == KeyValue.Invalid)
            {
                return result;
            }

            foreach (var depotSection in mainAppDepots.Children)
            {
                if (!uint.TryParse(depotSection.Name, out var depotId))
                {
                    continue;
                }

                // DLC depots are listed in the main app's PICS depots tree, but
                // should be controlled by the DLC prompt rather than the main-depot prompt.
                if (depotSection["dlcappid"] != KeyValue.Invalid)
                {
                    continue;
                }

                // Filter out shared depots according to Steam PICS metadata.
                if (IsSharedDepot(depotSection, appId))
                {
                    continue;
                }

                // Apply platform filter (mirrors ContentDownloader.cs depot-loop filter).
                var cfg = depotSection["config"];
                if (!DepotMatchesPlatform(cfg, os, allPlatforms, arch, allArchs, language, allLanguages))
                {
                    continue;
                }
                // Depot with no `config` block at all is platform-agnostic — kept unconditionally.

                result.Add(depotId);
            }

            return result;
        }

        internal static bool IsSharedDepot(KeyValue depotSection, uint appId)
        {
            if (depotSection == null || depotSection == KeyValue.Invalid)
            {
                return false;
            }

            var sharedInstall = depotSection["sharedinstall"];
            if (sharedInstall != KeyValue.Invalid && sharedInstall.AsUnsignedInteger() != 0)
            {
                return true;
            }

            var depotFromApp = depotSection["depotfromapp"];
            if (depotFromApp == KeyValue.Invalid)
            {
                return false;
            }

            var fromApp = depotFromApp.AsUnsignedInteger();
            return fromApp != 0 && fromApp != appId;
        }

        public static List<uint> ComputeDlcCandidates(
            IEnumerable<uint> luaOwnedAppIds,
            uint appId,
            KeyValue mainAppDepots,
            KeyValue mainAppExtended)
        {
            var result = new List<uint>();
            if (luaOwnedAppIds == null)
            {
                return result;
            }

            var listedDlcAppIds = ExtractListedDlcAppIds(mainAppExtended);

            foreach (var id in luaOwnedAppIds)
            {
                if (id == appId)
                {
                    continue;
                }

                if (listedDlcAppIds.Contains(id) || HasDlcDepotMarker(id, mainAppDepots))
                {
                    result.Add(id);
                }
            }

            result.Sort();
            return result;
        }

        internal static bool DepotBelongsToDlc(uint depotId, uint dlcAppId, KeyValue mainAppDepots)
        {
            if (depotId == dlcAppId)
            {
                return true;
            }

            if (mainAppDepots == null || mainAppDepots == KeyValue.Invalid)
            {
                return false;
            }

            var depotSection = mainAppDepots[depotId.ToString()];
            if (depotSection == KeyValue.Invalid)
            {
                return false;
            }

            var dlcAppIdKv = depotSection["dlcappid"];
            return dlcAppIdKv != KeyValue.Invalid && dlcAppIdKv.AsUnsignedInteger() == dlcAppId;
        }

        static bool HasDlcDepotMarker(uint appOrDepotId, KeyValue mainAppDepots)
        {
            if (mainAppDepots == null || mainAppDepots == KeyValue.Invalid)
            {
                return false;
            }

            var depotSection = mainAppDepots[appOrDepotId.ToString()];
            if (depotSection == KeyValue.Invalid)
            {
                return false;
            }

            var dlcAppIdKv = depotSection["dlcappid"];
            return dlcAppIdKv != KeyValue.Invalid && dlcAppIdKv.AsUnsignedInteger() == appOrDepotId;
        }

        static HashSet<uint> ExtractListedDlcAppIds(KeyValue mainAppExtended)
        {
            var result = new HashSet<uint>();
            if (mainAppExtended == null || mainAppExtended == KeyValue.Invalid)
            {
                return result;
            }

            var listOfDlc = mainAppExtended["listofdlc"].AsString();
            if (string.IsNullOrWhiteSpace(listOfDlc))
            {
                return result;
            }

            foreach (var part in listOfDlc.Split(','))
            {
                if (uint.TryParse(part.Trim(), out var dlcAppId))
                {
                    result.Add(dlcAppId);
                }
            }

            return result;
        }

        // Pure helper — tested. Returns true if the depot's config block matches the
        // user's platform selection. A depot with no config (KeyValue.Invalid) or with
        // empty/missing oslist/osarch/language values is treated as platform-agnostic
        // and matches. When a depot specifies a value the user did not pass an all*
        // override for, returns false only on mismatch (ordinal comparison after trim).
        internal static bool DepotMatchesPlatform(
            KeyValue depotConfig,
            string os,
            bool allPlatforms,
            string arch,
            bool allArchs,
            string language,
            bool allLanguages)
        {
            if (depotConfig == null || depotConfig == KeyValue.Invalid)
            {
                return true;
            }

            if (!allPlatforms)
            {
                var oslist = depotConfig["oslist"];
                if (oslist != KeyValue.Invalid && !string.IsNullOrWhiteSpace(oslist.Value))
                {
                    var arr = oslist.Value.Split(',');
                    var hit = false;
                    foreach (var v in arr)
                    {
                        if (string.Equals(v.Trim(), os, StringComparison.Ordinal))
                        {
                            hit = true;
                            break;
                        }
                    }
                    if (!hit)
                    {
                        return false;
                    }
                }
            }

            if (!allArchs)
            {
                var osarch = depotConfig["osarch"];
                if (osarch != KeyValue.Invalid && !string.IsNullOrWhiteSpace(osarch.Value))
                {
                    if (!string.Equals(osarch.Value.Trim(), arch, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            if (!allLanguages)
            {
                var langKv = depotConfig["language"];
                if (langKv != KeyValue.Invalid && !string.IsNullOrWhiteSpace(langKv.Value))
                {
                    if (!string.Equals(langKv.Value.Trim(), language, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public static PlatformSelection PromptPlatform(KeyValue mainAppDepots)
        {
            return PromptPlatform(mainAppDepots, null);
        }

        public static PlatformSelection PromptPlatform(KeyValue mainAppDepots, KeyValue mainAppCommon)
        {
            var (osChoices, archChoices, languageChoices) = ExtractPlatformChoices(mainAppDepots, mainAppCommon);

            bool allPlatforms = false;
            string osPick = osChoices.Count == 1 ? osChoices[0] : null;
            bool allArchs = false;
            string archPick = archChoices.Count == 1 ? archChoices[0] : null;
            bool allLanguages = languageChoices.Count == 0;
            string languagePick = null;

            if (osChoices.Count >= 2)
            {
                var choices = new List<string>(osChoices) { "All platforms" };
                var picked = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Target OS:")
                        .PageSize(GetPromptPageSize())
                        .AddChoices(choices));
                if (string.Equals(picked, "All platforms", StringComparison.Ordinal))
                {
                    allPlatforms = true;
                }
                else
                {
                    osPick = picked;
                }
            }

            if (archChoices.Count >= 2)
            {
                var choices = new List<string>(archChoices) { "All architectures" };
                var picked = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Target architecture:")
                        .PageSize(GetPromptPageSize())
                        .AddChoices(choices));
                if (string.Equals(picked, "All architectures", StringComparison.Ordinal))
                {
                    allArchs = true;
                }
                else
                {
                    archPick = picked;
                }
            }

            if (languageChoices.Count >= 1)
            {
                var choices = new List<string>(languageChoices) { "All languages" };
                var picked = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Target language:")
                        .PageSize(GetPromptPageSize())
                        .AddChoices(choices));
                if (string.Equals(picked, "All languages", StringComparison.Ordinal))
                {
                    allLanguages = true;
                }
                else
                {
                    languagePick = picked;
                }
            }

            return new PlatformSelection
            {
                AllPlatforms = allPlatforms,
                Os = osPick,
                AllArchs = allArchs,
                Arch = archPick,
                AllLanguages = allLanguages,
                Language = languagePick,
            };
        }

        public static IReadOnlyList<uint> PromptMainDepots(
            IReadOnlyList<uint> candidates,
            KeyValue mainAppDepots)
        {
            if (candidates.Count <= 1)
            {
                return candidates;
            }

            System.Console.Write("The app has {0} main depots. Customize selection? [y/N]: ", candidates.Count);
            var input = (System.Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
            if (input != "y" && input != "yes")
            {
                return candidates;
            }

            var prompt = new MultiSelectionPrompt<uint>()
                .Title("Select main depots to install (default: all):")
                .NotRequired()
                .PageSize(GetPromptPageSize())
                .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to confirm)[/]")
                .UseConverter(id => FormatMainDepotForDisplay(id, mainAppDepots));

            foreach (var id in candidates)
            {
                prompt.AddChoice(id).Select();
            }

            return AnsiConsole.Prompt(prompt);
        }

        static string FormatMainDepotForDisplay(uint depotId, KeyValue mainAppDepots)
        {
            var section = mainAppDepots[depotId.ToString()];
            if (section == KeyValue.Invalid)
            {
                return $"depot {depotId}";
            }

            var name = section["name"].AsString() ?? string.Empty;
            var osQualifier = section["config"]["oslist"].AsString() ?? "any";

            if (string.IsNullOrWhiteSpace(name))
            {
                return $"depot {depotId}  ({osQualifier})";
            }
            return $"depot {depotId}  {name}  ({osQualifier})";
        }

        public static IReadOnlyList<uint> PromptDlcs(IReadOnlyList<uint> dlcAppIds, System.Func<uint, string> nameResolver)
        {
            if (dlcAppIds.Count == 0)
            {
                return dlcAppIds;
            }

            var prompt = new MultiSelectionPrompt<uint>()
                .Title("Select DLCs to install (default: all):")
                .NotRequired()
                .PageSize(GetPromptPageSize())
                .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to confirm)[/]")
                .UseConverter(id =>
                {
                    var name = nameResolver?.Invoke(id);
                    return string.IsNullOrWhiteSpace(name)
                        ? $"app {id}"
                        : $"app {id}  {name}";
                });

            foreach (var id in dlcAppIds)
            {
                prompt.AddChoice(id).Select();
            }

            return AnsiConsole.Prompt(prompt);
        }

        static int GetPromptPageSize()
        {
            const int fallbackHeight = 24;
            const int reservedLines = 8;
            const int preferredMinPageSize = 10;
            const int absoluteMinPageSize = 3;
            const int maxPageSize = 50;

            var height = GetCurrentConsoleHeight(fallbackHeight);
            var availableLines = Math.Max(absoluteMinPageSize, height - reservedLines);
            if (availableLines < preferredMinPageSize)
            {
                return availableLines;
            }

            return Math.Min(availableLines, maxPageSize);
        }

        static int GetCurrentConsoleHeight(int fallbackHeight)
        {
            try
            {
                if (!Console.IsOutputRedirected && Console.WindowHeight > 0)
                {
                    return Console.WindowHeight;
                }
            }
            catch
            {
                // Some redirected or hosted terminals cannot report a live window height.
            }

            return AnsiConsole.Profile.Height > 0 ? AnsiConsole.Profile.Height : fallbackHeight;
        }
    }
}
