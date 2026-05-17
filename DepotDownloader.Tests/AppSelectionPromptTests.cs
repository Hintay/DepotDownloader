// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Collections.Generic;
using DepotDownloader;
using Xunit;

namespace DepotDownloader.Tests
{
    public class AppSelectionPromptTests
    {
        [Fact]
        public void Lua_OneArgAddAppId_CapturesIntoOwnedApps()
        {
            const string lua = "addappid(601150)";

            var data = DepotKeyStore.AddFromLua(lua);

            Assert.Contains(601150u, data.OwnedApps);
        }

        [Fact]
        public void Lua_ThreeArgAddAppId_CapturesIntoOwnedApps()
        {
            const string lua = "addappid(601151, 1, \"deadbeef\")";

            var data = DepotKeyStore.AddFromLua(lua);

            Assert.Contains(601151u, data.OwnedApps);
        }

        [Fact]
        public void Lua_OneArgFollowedByThreeArg_CapturesOnceOwnedAppKeyRegistered()
        {
            const string lua = "addappid(940500)\naddappid(940500, 1, \"deadbeef\")";

            var data = DepotKeyStore.AddFromLua(lua);

            Assert.Single(data.OwnedApps);
            Assert.Contains(940500u, data.OwnedApps);
            Assert.True(DepotKeyStore.ContainsKey(940500u));
        }
    }
}
