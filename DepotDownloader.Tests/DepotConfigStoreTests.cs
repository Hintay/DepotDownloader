// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using DepotDownloader;
using Xunit;

namespace DepotDownloader.Tests
{
    [Collection("DepotConfigStoreSingleton")]
    public class DepotConfigStoreTests : IDisposable
    {
        readonly string tempFile;

        public DepotConfigStoreTests()
        {
            tempFile = Path.Combine(Path.GetTempPath(), $"dd-cfg-{Guid.NewGuid():N}.bin");
            DepotConfigStore.Instance = null;
        }

        public void Dispose()
        {
            DepotConfigStore.Instance = null;
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public void RoundTrip_PreservesAllThreeFields()
        {
            DepotConfigStore.LoadFromFile(tempFile);
            DepotConfigStore.Instance.AppConfigs[12345] = new AppDownloadConfig
            {
                Os = "windows",
                Arch = "64",
                Language = "english",
            };
            DepotConfigStore.Save();

            DepotConfigStore.Instance = null;
            DepotConfigStore.LoadFromFile(tempFile);

            Assert.True(DepotConfigStore.Instance.AppConfigs.TryGetValue(12345, out var cfg));
            Assert.Equal("windows", cfg.Os);
            Assert.Equal("64", cfg.Arch);
            Assert.Equal("english", cfg.Language);
        }

        [Fact]
        public void RoundTrip_PreservesAllNulls()
        {
            DepotConfigStore.LoadFromFile(tempFile);
            DepotConfigStore.Instance.AppConfigs[12345] = new AppDownloadConfig
            {
                Os = null,
                Arch = null,
                Language = null,
            };
            DepotConfigStore.Save();

            DepotConfigStore.Instance = null;
            DepotConfigStore.LoadFromFile(tempFile);

            Assert.True(DepotConfigStore.Instance.AppConfigs.TryGetValue(12345, out var cfg));
            Assert.Null(cfg.Os);
            Assert.Null(cfg.Arch);
            Assert.Null(cfg.Language);
        }

        [Fact]
        public void RoundTrip_MixedNullAndValue()
        {
            DepotConfigStore.LoadFromFile(tempFile);
            DepotConfigStore.Instance.AppConfigs[12345] = new AppDownloadConfig
            {
                Os = "linux",
                Arch = null,
                Language = "german",
            };
            DepotConfigStore.Save();

            DepotConfigStore.Instance = null;
            DepotConfigStore.LoadFromFile(tempFile);

            Assert.True(DepotConfigStore.Instance.AppConfigs.TryGetValue(12345, out var cfg));
            Assert.Equal("linux", cfg.Os);
            Assert.Null(cfg.Arch);
            Assert.Equal("german", cfg.Language);
        }

        [Fact]
        public void BackwardCompat_OldFileWithOnlyInstalledManifestIDsLoads()
        {
            DepotConfigStore.LoadFromFile(tempFile);
            DepotConfigStore.Instance.InstalledManifestIDs[999] = 123456789UL;
            DepotConfigStore.Save();

            DepotConfigStore.Instance = null;
            DepotConfigStore.LoadFromFile(tempFile);

            Assert.NotNull(DepotConfigStore.Instance.AppConfigs);
            Assert.Empty(DepotConfigStore.Instance.AppConfigs);
            Assert.Equal(123456789UL, DepotConfigStore.Instance.InstalledManifestIDs[999]);
        }

        [Fact]
        public void RoundTrip_MultipleApps()
        {
            DepotConfigStore.LoadFromFile(tempFile);
            DepotConfigStore.Instance.AppConfigs[111] = new AppDownloadConfig { Os = "windows", Arch = "64", Language = "english" };
            DepotConfigStore.Instance.AppConfigs[222] = new AppDownloadConfig { Os = "linux", Arch = null, Language = "russian" };
            DepotConfigStore.Save();

            DepotConfigStore.Instance = null;
            DepotConfigStore.LoadFromFile(tempFile);

            Assert.Equal(2, DepotConfigStore.Instance.AppConfigs.Count);
            Assert.Equal("windows", DepotConfigStore.Instance.AppConfigs[111].Os);
            Assert.Equal("linux", DepotConfigStore.Instance.AppConfigs[222].Os);
            Assert.Null(DepotConfigStore.Instance.AppConfigs[222].Arch);
            Assert.Equal("russian", DepotConfigStore.Instance.AppConfigs[222].Language);
        }
    }
}
