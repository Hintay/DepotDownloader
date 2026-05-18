// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ProtoBuf;

namespace DepotDownloader
{
    [ProtoContract]
    class DepotConfigStore
    {
        [ProtoMember(1)]
        public Dictionary<uint, ulong> InstalledManifestIDs { get; private set; }

        [ProtoMember(2)]
        public Dictionary<uint, AppDownloadConfig> AppConfigs { get; private set; }

        string FileName;

        DepotConfigStore()
        {
            InstalledManifestIDs = [];
            AppConfigs = [];
        }

        static bool Loaded
        {
            get { return Instance != null; }
        }

        public static DepotConfigStore Instance;

        public static string ConfigDirectory
        {
            get
            {
                if (!Loaded)
                    throw new Exception("Read config directory before loading");

                return Path.GetDirectoryName(Instance.FileName);
            }
        }

        public static void LoadFromFile(string filename)
        {
            if (Loaded)
                throw new Exception("Config already loaded");

            if (File.Exists(filename))
            {
                using var fs = File.Open(filename, FileMode.Open);
                using var ds = new DeflateStream(fs, CompressionMode.Decompress);
                Instance = Serializer.Deserialize<DepotConfigStore>(ds);

                // Backward compat: old depot.config files lack ProtoMember(2),
                // and protobuf-net invokes the private ctor through reflection
                // bypassing field initializers in some paths.
                Instance.InstalledManifestIDs ??= [];
                Instance.AppConfigs ??= [];
            }
            else
            {
                Instance = new DepotConfigStore();
            }

            Instance.FileName = filename;
        }

        public static void Save()
        {
            if (!Loaded)
                throw new Exception("Saved config before loading");

            using var fs = File.Open(Instance.FileName, FileMode.Create);
            using var ds = new DeflateStream(fs, CompressionMode.Compress);
            Serializer.Serialize(ds, Instance);
        }
    }
}
