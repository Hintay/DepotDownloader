// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using DepotDownloader;
using Xunit;

namespace DepotDownloader.Tests
{
    public class AppManifestRoundtripTests
    {
        [Fact]
        public void Writer_And_Reader_RoundTrip_PreservesAllFields()
        {
            var depots = new List<SteamAppManifestDepot>
            {
                new(601151, 5034340940015103443UL),
                new(601152, 37045512487732505UL),
                new(940500, 7413960716303235231UL),
            };
            var manifest = new SteamAppManifest(
                appId: 601150,
                name: "Devil May Cry 5",
                installDir: "Devil May Cry 5",
                buildId: 16243,
                language: "english",
                stateFlags: 4,
                depots: depots);

            var path = Path.Combine(Path.GetTempPath(), $"acf-roundtrip-{Guid.NewGuid():N}.acf");
            try
            {
                AppManifestWriter.WriteToFile(path, manifest);

                var read = AppManifestReader.TryReadFromFile(path);

                Assert.NotNull(read);
                Assert.Equal(601150u, read.AppId);
                Assert.Equal(4u, read.StateFlags);
                Assert.Equal(16243u, read.BuildId);
                Assert.Equal(3, read.InstalledDepots.Count);
                Assert.Equal(5034340940015103443UL, read.InstalledDepots[601151]);
                Assert.Equal(37045512487732505UL, read.InstalledDepots[601152]);
                Assert.Equal(7413960716303235231UL, read.InstalledDepots[940500]);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Writer_RoundTrips_StateFlags_1026()
        {
            var manifest = new SteamAppManifest(
                appId: 601150,
                name: "Test",
                installDir: "Test",
                buildId: 0,
                language: "english",
                stateFlags: 1026,
                depots: new List<SteamAppManifestDepot> { new(601151, 1UL) });

            var path = Path.Combine(Path.GetTempPath(), $"acf-1026-{Guid.NewGuid():N}.acf");
            try
            {
                AppManifestWriter.WriteToFile(path, manifest);
                var read = AppManifestReader.TryReadFromFile(path);

                Assert.NotNull(read);
                Assert.Equal(1026u, read.StateFlags);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Reader_ReturnsNull_WhenFileMissing()
        {
            var path = Path.Combine(Path.GetTempPath(), $"never-exists-{Guid.NewGuid():N}.acf");
            Assert.False(File.Exists(path));

            var result = AppManifestReader.TryReadFromFile(path);

            Assert.Null(result);
        }

        [Fact]
        public void Reader_ReturnsNull_WhenPathEmpty()
        {
            Assert.Null(AppManifestReader.TryReadFromFile(null));
            Assert.Null(AppManifestReader.TryReadFromFile(""));
            Assert.Null(AppManifestReader.TryReadFromFile("   "));
        }

        [Fact]
        public void Reader_ReturnsNull_WhenRootIsNotAppState()
        {
            var path = Path.Combine(Path.GetTempPath(), $"acf-wrongroot-{Guid.NewGuid():N}.acf");
            try
            {
                File.WriteAllText(path, "\"NotAppState\"\n{\n\t\"appid\"\t\t\"601150\"\n}\n");

                var result = AppManifestReader.TryReadFromFile(path);

                Assert.Null(result);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Reader_ReturnsNull_WhenFileIsCorrupt()
        {
            var path = Path.Combine(Path.GetTempPath(), $"acf-corrupt-{Guid.NewGuid():N}.acf");
            try
            {
                File.WriteAllText(path, "not a vdf file at all { [ } }");

                var result = AppManifestReader.TryReadFromFile(path);

                Assert.Null(result);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
