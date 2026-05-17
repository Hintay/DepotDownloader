// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Collections.Generic;
using DepotDownloader;
using SteamKit2;
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

        // --- ExtractPlatformChoices ---

        [Fact]
        public void ExtractPlatformChoices_EmptyDepots_ReturnsEmpty()
        {
            var depots = new KeyValue("depots");

            var (os, arch, language) = AppSelectionPrompt.ExtractPlatformChoices(depots);

            Assert.Empty(os);
            Assert.Empty(arch);
            Assert.Empty(language);
        }

        [Fact]
        public void ExtractPlatformChoices_DepotWithCommaSeparatedOslist_SplitsAndIncludesAll()
        {
            var depots = new KeyValue("depots");
            depots.Children.Add(BuildDepotKv("601151", oslist: "windows,linux"));

            var (os, _, _) = AppSelectionPrompt.ExtractPlatformChoices(depots);

            Assert.Contains("windows", os);
            Assert.Contains("linux", os);
        }

        [Fact]
        public void ExtractPlatformChoices_DepotWithOsArchAndLanguage_PopulatesAllThreeSets()
        {
            var depots = new KeyValue("depots");
            depots.Children.Add(BuildDepotKv("601151", oslist: "windows", osarch: "64", language: "english"));

            var (os, arch, language) = AppSelectionPrompt.ExtractPlatformChoices(depots);

            Assert.Equal(new[] { "windows" }, os);
            Assert.Equal(new[] { "64" }, arch);
            Assert.Equal(new[] { "english" }, language);
        }

        [Fact]
        public void ExtractPlatformChoices_MultipleDepots_UnionsDistinct()
        {
            var depots = new KeyValue("depots");
            depots.Children.Add(BuildDepotKv("601151", oslist: "windows", osarch: "64", language: "english"));
            depots.Children.Add(BuildDepotKv("601152", oslist: "macos", osarch: "64", language: "schinese"));
            depots.Children.Add(BuildDepotKv("601153", oslist: "windows", osarch: "32", language: "english"));

            var (os, arch, language) = AppSelectionPrompt.ExtractPlatformChoices(depots);

            // Each set should be sorted ordinal and deduplicated.
            Assert.Equal(new[] { "macos", "windows" }, os);
            Assert.Equal(new[] { "32", "64" }, arch);
            Assert.Equal(new[] { "english", "schinese" }, language);
        }

        [Fact]
        public void ExtractPlatformChoices_DepotWithoutConfig_IsIgnored()
        {
            var depots = new KeyValue("depots");
            var depotKv = new KeyValue("601151");  // no "config" child at all
            depots.Children.Add(depotKv);

            var (os, _, _) = AppSelectionPrompt.ExtractPlatformChoices(depots);

            Assert.Empty(os);
        }

        [Fact]
        public void ExtractPlatformChoices_WhitespaceValues_AreIgnored()
        {
            var depots = new KeyValue("depots");
            depots.Children.Add(BuildDepotKv("601151", oslist: "  ", osarch: "", language: "   "));

            var (os, arch, language) = AppSelectionPrompt.ExtractPlatformChoices(depots);

            Assert.Empty(os);
            Assert.Empty(arch);
            Assert.Empty(language);
        }

        // Helper: build a depot KV node like PICS would return:
        // "601151" {
        //   "config" {
        //     "oslist"   "windows"
        //     "osarch"   "64"
        //     "language" "english"
        //   }
        // }
        static KeyValue BuildDepotKv(string depotIdName, string oslist = null, string osarch = null, string language = null)
        {
            var kv = new KeyValue(depotIdName);
            var cfg = new KeyValue("config");
            if (oslist != null)   cfg.Children.Add(new KeyValue("oslist", oslist));
            if (osarch != null)   cfg.Children.Add(new KeyValue("osarch", osarch));
            if (language != null) cfg.Children.Add(new KeyValue("language", language));
            kv.Children.Add(cfg);
            return kv;
        }
    }
}
