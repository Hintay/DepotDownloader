// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using DepotDownloader;
using Xunit;

namespace DepotDownloader.Tests
{
    public sealed class ProgramLuaFileResolutionTests : IDisposable
    {
        readonly string scratch;
        readonly string originalCurrentDirectory;

        public ProgramLuaFileResolutionTests()
        {
            scratch = Path.Combine(Path.GetTempPath(), $"dd-lua-resolve-{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratch);
            originalCurrentDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(scratch);
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(originalCurrentDirectory);
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }

        [Fact]
        public void ResolveLuaFile_NoManifestDir_UsesCurrentDirectoryAppIdFolder()
        {
            const uint appId = 601150u;
            var appDir = Path.Combine(scratch, appId.ToString());
            var luaPath = Path.Combine(appDir, $"{appId}.lua");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(luaPath, "addappid(601150)");

            var resolved = Program.ResolveLuaFile(appId, luaFile: null, useManifestDirectory: false, manifestDirectory: null);

            Assert.Equal(luaPath, resolved);
        }

        [Fact]
        public void ResolveMidOverridesFile_NoExplicitPath_UsesLuaDirectoryShortUnderscoreName()
        {
            var luaDir = Path.Combine(scratch, "1721470");
            Directory.CreateDirectory(luaDir);
            var luaPath = Path.Combine(luaDir, "1721470.lua");
            var overridesPath = Path.Combine(luaDir, "mid_overrides.json");
            File.WriteAllText(luaPath, "addappid(1721470)");
            File.WriteAllText(overridesPath, "{}");

            var resolved = Program.ResolveMidOverridesFile(null, luaPath);

            Assert.Equal(overridesPath, resolved);
        }

        [Fact]
        public void LoadMidOverrides_AcceptsNumberAndStringManifestIds()
        {
            var path = Path.Combine(scratch, "mid_overrides.json");
            File.WriteAllText(path, """
            {
              "1817491": 2468627520310452088,
              "2555198": "7976244025258826224"
            }
            """);

            var overrides = Program.LoadMidOverrides(path);

            Assert.Equal(2468627520310452088UL, overrides[1817491u]);
            Assert.Equal(7976244025258826224UL, overrides[2555198u]);
        }

        [Fact]
        public void GetLuaBatchDepotManifestIds_IgnoreLuaManifestIds_UsesLuaKeyDepotIdsWithLatestManifest()
        {
            var config = new DownloadConfig
            {
                IgnoreLuaManifestIds = true,
                LuaManifestIds = [],
                LuaKeyDepotIds = [601151u],
            };

            var depotManifestIds = Program.GetLuaBatchDepotManifestIds(config);

            var entry = Assert.Single(depotManifestIds);
            Assert.Equal(601151u, entry.depotId);
            Assert.Equal(ContentDownloader.INVALID_MANIFEST_ID, entry.manifestId);
        }

        [Fact]
        public void GetLuaBatchDepotManifestIds_IgnoreLuaManifestIds_DoesNotUseSetManifestDepotIds()
        {
            var config = new DownloadConfig
            {
                IgnoreLuaManifestIds = true,
                LuaManifestIds = [],
                LuaKeyDepotIds = [],
            };

            var depotManifestIds = Program.GetLuaBatchDepotManifestIds(config);

            Assert.Empty(depotManifestIds);
        }

        [Fact]
        public void GetLuaBatchDepotManifestIds_MidOverrideWinsWhenIgnoringLuaManifestIds()
        {
            var config = new DownloadConfig
            {
                IgnoreLuaManifestIds = true,
                LuaManifestIds = [],
                LuaKeyDepotIds = [1817491u, 2555198u],
                ManifestIdOverrides = new()
                {
                    [1817491u] = 2468627520310452088UL,
                },
            };

            var depotManifestIds = Program.GetLuaBatchDepotManifestIds(config);

            Assert.Equal(
                new[]
                {
                    (depotId: 1817491u, manifestId: 2468627520310452088UL),
                    (depotId: 2555198u, manifestId: ContentDownloader.INVALID_MANIFEST_ID),
                },
                depotManifestIds);
        }

        [Fact]
        public void ShouldLoadNewManifestFromDirectory_IgnoreLuaManifestIds_ReturnsFalse()
        {
            var config = new DownloadConfig
            {
                UseManifestDirectory = true,
                IgnoreLuaManifestIds = true,
            };

            Assert.False(ContentDownloader.ShouldLoadNewManifestFromDirectory(config));
        }

        [Fact]
        public void ShouldLoadNewManifestFromDirectory_ManifestDirectoryWithoutIgnoreLuaManifestIds_ReturnsTrue()
        {
            var config = new DownloadConfig
            {
                UseManifestDirectory = true,
                IgnoreLuaManifestIds = false,
            };

            Assert.True(ContentDownloader.ShouldLoadNewManifestFromDirectory(config));
        }

        [Fact]
        public void GetSkippedManifestDirectoryCacheMessage_IncludesDepotAndReason()
        {
            var message = ContentDownloader.GetSkippedManifestDirectoryCacheMessage(1817491u);

            Assert.Equal("Skipping manifestdir manifest cache for depot 1817491 because -no-lua-mid is enabled.", message);
        }

        [Fact]
        public void GetDownloadingManifestMessage_IncludesDepotAndManifest()
        {
            var message = ContentDownloader.GetDownloadingManifestMessage(1817491u, 2957411760343078601UL);

            Assert.Equal("Downloading depot 1817491 manifest 2957411760343078601 from Steam/CDN.", message);
        }

        [Fact]
        public void GetResolvedLatestManifestMessage_IncludesDepotOwnerBranchAndManifest()
        {
            var message = ContentDownloader.GetResolvedLatestManifestMessage(1817491u, 1817490u, "public", 2468627520310452088UL);

            Assert.Equal("Resolved latest manifest for depot 1817491 from app 1817490 branch 'public': 2468627520310452088.", message);
        }

        [Fact]
        public void GetInstalledManifestComparisonMessage_IncludesInstalledAndTargetManifest()
        {
            var message = ContentDownloader.GetInstalledManifestComparisonMessage(1817491u, 2957411760343078601UL, 2468627520310452088UL);

            Assert.Equal("Appmanifest comparison for depot 1817491: installed manifest 2957411760343078601, target manifest 2468627520310452088.", message);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void ShouldLogInstalledManifestComparison_OnlyWhenDebugEnabled(bool debugEnabled, bool expected)
        {
            Assert.Equal(expected, ContentDownloader.ShouldLogInstalledManifestComparison(debugEnabled));
        }

        [Fact]
        public void ShouldSkipFullyInstalledApp_OldAppmanifestManifestAndNewSteamManifest_ReturnsFalse()
        {
            var installedDepots = new Dictionary<uint, ulong>
            {
                [1817491u] = 2957411760343078601UL,
            };
            var targetDepots = new List<(uint depotId, ulong manifestId)>
            {
                (1817491u, 2468627520310452088UL),
            };

            var shouldSkip = ContentDownloader.ShouldSkipFullyInstalledApp(installedDepots, targetDepots, verifyAll: false);

            Assert.False(shouldSkip);
        }

        [Fact]
        public void ShouldSkipFullyInstalledApp_AppmanifestMatchesSteamManifest_ReturnsTrue()
        {
            var installedDepots = new Dictionary<uint, ulong>
            {
                [1817491u] = 2468627520310452088UL,
            };
            var targetDepots = new List<(uint depotId, ulong manifestId)>
            {
                (1817491u, 2468627520310452088UL),
            };

            var shouldSkip = ContentDownloader.ShouldSkipFullyInstalledApp(installedDepots, targetDepots, verifyAll: false);

            Assert.True(shouldSkip);
        }

        [Fact]
        public void GetLuaBatchDepotManifestIds_UsesAddAppIdKeyDepotsAndOnlyAppliesMatchingManifestIds()
        {
            var config = new DownloadConfig
            {
                IgnoreLuaManifestIds = false,
                LuaManifestIds = new()
                {
                    [601151u] = 111UL,
                    [601152u] = 222UL,
                },
                LuaKeyDepotIds = [601151u, 601153u],
            };

            var depotManifestIds = Program.GetLuaBatchDepotManifestIds(config);

            Assert.Equal(
                new[]
                {
                    (depotId: 601151u, manifestId: 111UL),
                    (depotId: 601153u, manifestId: ContentDownloader.INVALID_MANIFEST_ID),
                },
                depotManifestIds);
        }
    }
}
