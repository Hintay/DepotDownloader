// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

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
            var osSet       = new System.Collections.Generic.SortedSet<string>(System.StringComparer.Ordinal);
            var archSet     = new System.Collections.Generic.SortedSet<string>(System.StringComparer.Ordinal);
            var languageSet = new System.Collections.Generic.SortedSet<string>(System.StringComparer.Ordinal);

            if (mainAppDepots == null || mainAppDepots == KeyValue.Invalid)
            {
                return (new List<string>(), new List<string>(), new List<string>());
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

                // Filter out shared depots (pointing at a different app).
                var depotFromApp = depotSection["depotfromapp"];
                if (depotFromApp != KeyValue.Invalid)
                {
                    var fromApp = depotFromApp.AsUnsignedInteger();
                    if (fromApp != 0 && fromApp != appId)
                    {
                        continue;
                    }
                }

                // Apply platform filter (mirrors ContentDownloader.cs depot-loop filter).
                var cfg = depotSection["config"];
                if (cfg != KeyValue.Invalid)
                {
                    if (!allPlatforms)
                    {
                        var oslist = cfg["oslist"];
                        if (oslist != KeyValue.Invalid && !string.IsNullOrWhiteSpace(oslist.Value))
                        {
                            var arr = oslist.Value.Split(',');
                            var hit = false;
                            foreach (var v in arr)
                            {
                                if (string.Equals(v.Trim(), os, System.StringComparison.Ordinal))
                                {
                                    hit = true;
                                    break;
                                }
                            }
                            if (!hit)
                            {
                                continue;
                            }
                        }
                    }

                    if (!allArchs)
                    {
                        var osarch = cfg["osarch"];
                        if (osarch != KeyValue.Invalid && !string.IsNullOrWhiteSpace(osarch.Value))
                        {
                            if (!string.Equals(osarch.Value.Trim(), arch, System.StringComparison.Ordinal))
                            {
                                continue;
                            }
                        }
                    }

                    if (!allLanguages)
                    {
                        var langKv = cfg["language"];
                        if (langKv != KeyValue.Invalid && !string.IsNullOrWhiteSpace(langKv.Value))
                        {
                            if (!string.Equals(langKv.Value.Trim(), language, System.StringComparison.Ordinal))
                            {
                                continue;
                            }
                        }
                    }
                }
                // Depot with no `config` block at all is platform-agnostic — kept unconditionally.

                result.Add(depotId);
            }

            return result;
        }

        public static PlatformSelection PromptPlatform(KeyValue mainAppDepots)
        {
            var (osChoices, archChoices, languageChoices) = ExtractPlatformChoices(mainAppDepots);

            bool allPlatforms = false;
            string osPick = null;
            bool allArchs = false;
            string archPick = null;
            bool allLanguages = false;
            string languagePick = null;

            if (osChoices.Count >= 2)
            {
                var choices = new List<string>(osChoices) { "All platforms" };
                var picked = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Target OS:")
                        .PageSize(10)
                        .AddChoices(choices));
                if (string.Equals(picked, "All platforms", System.StringComparison.Ordinal))
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
                        .PageSize(10)
                        .AddChoices(choices));
                if (string.Equals(picked, "All architectures", System.StringComparison.Ordinal))
                {
                    allArchs = true;
                }
                else
                {
                    archPick = picked;
                }
            }

            if (languageChoices.Count >= 2)
            {
                var choices = new List<string>(languageChoices) { "All languages" };
                var picked = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Target language:")
                        .PageSize(15)
                        .AddChoices(choices));
                if (string.Equals(picked, "All languages", System.StringComparison.Ordinal))
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
                .PageSize(15)
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

        public static IReadOnlyList<uint> PromptDlcs(IReadOnlyList<uint> dlcAppIds)
        {
            return dlcAppIds;
        }
    }
}
