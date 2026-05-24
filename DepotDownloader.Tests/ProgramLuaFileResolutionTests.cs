// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
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
