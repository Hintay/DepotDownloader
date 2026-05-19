// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using SteamKit2;

namespace DepotDownloader
{
    sealed class SteamAppManifestDepot(uint depotId, ulong manifestId, ulong? sizeOnDisk = null)
    {
        public uint DepotId { get; } = depotId;
        public ulong ManifestId { get; } = manifestId;
        public ulong? SizeOnDisk { get; } = sizeOnDisk;
    }

    sealed class SteamAppManifest(
        uint appId,
        string name,
        string installDir,
        uint buildId,
        string language,
        uint stateFlags,
        IReadOnlyCollection<SteamAppManifestDepot> depots)
    {
        public uint AppId { get; } = appId;
        public string Name { get; } = name;
        public string InstallDir { get; } = installDir;
        public uint BuildId { get; } = buildId;
        public string Language { get; } = language;
        public uint StateFlags { get; } = stateFlags;
        public IReadOnlyCollection<SteamAppManifestDepot> Depots { get; } = depots;
    }

    static class AppManifestWriter
    {
        public static void WriteToFile(string path, SteamAppManifest manifest)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var root = BuildKeyValue(manifest);
            root.SaveToFile(path, asBinary: false);
        }

        static KeyValue BuildKeyValue(SteamAppManifest manifest)
        {
            var root = new KeyValue("AppState");
            root.Children.Add(new KeyValue("appid", manifest.AppId.ToString()));
            root.Children.Add(new KeyValue("Universe", "1"));
            root.Children.Add(new KeyValue("StateFlags", manifest.StateFlags.ToString()));
            root.Children.Add(new KeyValue("name", manifest.Name));
            root.Children.Add(new KeyValue("installdir", manifest.InstallDir));

            if (manifest.Depots.Count > 0 && manifest.Depots.All(depot => depot.SizeOnDisk.HasValue))
            {
                var sizeOnDisk = manifest.Depots.Aggregate(0UL, (size, depot) => size + depot.SizeOnDisk.Value);
                root.Children.Add(new KeyValue("SizeOnDisk", sizeOnDisk.ToString()));
            }

            if (manifest.BuildId != 0)
            {
                root.Children.Add(new KeyValue("buildid", manifest.BuildId.ToString()));
                root.Children.Add(new KeyValue("TargetBuildID", manifest.BuildId.ToString()));
            }

            if (!string.IsNullOrWhiteSpace(manifest.Language))
            {
                var userConfig = new KeyValue("UserConfig");
                userConfig.Children.Add(new KeyValue("language", manifest.Language));
                root.Children.Add(userConfig);
            }

            var depots = new KeyValue("InstalledDepots");
            foreach (var depot in manifest.Depots.OrderBy(d => d.DepotId))
            {
                var depotKv = new KeyValue(depot.DepotId.ToString());
                depotKv.Children.Add(new KeyValue("manifest", depot.ManifestId.ToString()));
                if (depot.SizeOnDisk.HasValue)
                {
                    depotKv.Children.Add(new KeyValue("size", depot.SizeOnDisk.Value.ToString()));
                }
                depots.Children.Add(depotKv);
            }
            root.Children.Add(depots);

            return root;
        }
    }
}
