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
    }
}
