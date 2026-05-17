// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Collections.Generic;
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
            return (new List<string>(), new List<string>(), new List<string>());
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
            return new List<uint>();
        }

        public static PlatformSelection PromptPlatform(KeyValue mainAppDepots)
        {
            return new PlatformSelection();
        }

        public static IReadOnlyList<uint> PromptMainDepots(
            IReadOnlyList<uint> candidates,
            KeyValue mainAppDepots)
        {
            return candidates;
        }

        public static IReadOnlyList<uint> PromptDlcs(IReadOnlyList<uint> dlcAppIds)
        {
            return dlcAppIds;
        }
    }
}
