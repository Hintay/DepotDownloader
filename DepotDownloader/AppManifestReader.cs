// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using SteamKit2;

namespace DepotDownloader
{
    sealed class InstalledAppManifest(uint appId, uint stateFlags, uint buildId, IReadOnlyDictionary<uint, ulong> installedDepots)
    {
        // StateFlags value indicating the app is fully installed (no pending update,
        // interrupted download, or other in-progress state). Used by ContentDownloader
        // to distinguish a clean "nothing to do / verify" from a resume-from-interrupted
        // path. Mirrors Steam's own AppState_FullyInstalled = 4.
        public const uint StateFullyInstalled = 4;

        public uint AppId { get; } = appId;
        public uint StateFlags { get; } = stateFlags;
        public uint BuildId { get; } = buildId;
        public IReadOnlyDictionary<uint, ulong> InstalledDepots { get; } = installedDepots;
    }

    static class AppManifestReader
    {
        public static InstalledAppManifest TryReadFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            KeyValue root;
            try
            {
                root = KeyValue.LoadAsText(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Warning: failed to parse {0}: {1}", path, ex.Message);
                return null;
            }

            if (root == null || !string.Equals(root.Name, "AppState", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Warning: {0} root node is not \"AppState\"; treating as missing.", path);
                return null;
            }

            var appId = root["appid"].AsUnsignedInteger();
            if (appId == 0)
            {
                Console.WriteLine("Warning: {0} has no parseable appid; treating as missing.", path);
                return null;
            }

            var stateFlags = root["StateFlags"].AsUnsignedInteger();
            var buildId = root["buildid"].AsUnsignedInteger();

            var depots = new Dictionary<uint, ulong>();
            var installed = root["InstalledDepots"];
            if (installed != KeyValue.Invalid)
            {
                foreach (var depotKv in installed.Children)
                {
                    if (!uint.TryParse(depotKv.Name, out var depotId))
                    {
                        continue;
                    }

                    var manifestId = depotKv["manifest"].AsUnsignedLong();
                    depots[depotId] = manifestId;
                }
            }

            return new InstalledAppManifest(appId, stateFlags, buildId, depots);
        }
    }
}
