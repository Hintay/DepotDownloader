// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace DepotDownloader
{
    class ContentDownloaderException(string value) : Exception(value)
    {
    }

    static class ContentDownloader
    {
        public const uint INVALID_APP_ID = uint.MaxValue;
        public const uint INVALID_DEPOT_ID = uint.MaxValue;
        public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
        public const string DEFAULT_BRANCH = "public";

        public static DownloadConfig Config = new();

        private static Steam3Session steam3;
        private static CDNClientPool cdnPool;

        private const string DEFAULT_DOWNLOAD_DIR = "depots";
        private const string STEAMAPPS_DIR = "steamapps";
        private const string CONFIG_DIR = ".DepotDownloader";
        private static readonly string STAGING_DIR = Path.Combine(CONFIG_DIR, "staging");

        private static readonly FrozenSet<EWorkshopFileType> SupportedWorkshopFileTypes = FrozenSet.ToFrozenSet(new[]
        {
            EWorkshopFileType.Community,
            EWorkshopFileType.Art,
            EWorkshopFileType.Screenshot,
            EWorkshopFileType.Merch,
            EWorkshopFileType.IntegratedGuide,
            EWorkshopFileType.ControllerBinding,
        });

        private sealed class DepotDownloadInfo(
            uint depotid, uint appId, ulong manifestId, string branch,
            string installDir, byte[] depotKey)
        {
            public uint DepotId { get; } = depotid;
            public uint AppId { get; } = appId;
            public ulong ManifestId { get; } = manifestId;
            public string Branch { get; } = branch;
            public string InstallDir { get; } = installDir;
            public byte[] DepotKey { get; } = depotKey;
            public ulong? SizeOnDisk { get; set; }
        }

        static bool CreateDirectories(uint depotId, uint depotVersion, out string installDir)
        {
            installDir = null;
            try
            {
                if (string.IsNullOrWhiteSpace(Config.InstallDirectory))
                {
                    Directory.CreateDirectory(DEFAULT_DOWNLOAD_DIR);

                    var depotPath = Path.Combine(DEFAULT_DOWNLOAD_DIR, depotId.ToString());
                    Directory.CreateDirectory(depotPath);

                    installDir = Path.Combine(depotPath, depotVersion.ToString());
                    Directory.CreateDirectory(installDir);

                    Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                    Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
                }
                else
                {
                    Directory.CreateDirectory(Config.InstallDirectory);

                    installDir = Config.InstallDirectory;

                    Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
                    Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
                }
            }
            catch
            {
                return false;
            }

            return true;
        }

        static bool TestIsFileIncluded(string filename)
        {
            if (!Config.UsingFileList)
                return true;

            filename = filename.Replace('\\', '/');

            if (Config.FilesToDownload.Contains(filename))
            {
                return true;
            }

            foreach (var rgx in Config.FilesToDownloadRegex)
            {
                var m = rgx.Match(filename);

                if (m.Success)
                    return true;
            }

            return false;
        }

        static async Task<bool> AccountHasAccess(uint appId, uint depotId)
        {
            if (steam3 == null || steam3.steamUser.SteamID == null || (steam3.Licenses == null && steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser))
                return false;

            IEnumerable<uint> licenseQuery;
            if (steam3.steamUser.SteamID.AccountType == EAccountType.AnonUser)
            {
                licenseQuery = [17906];
            }
            else
            {
                licenseQuery = steam3.Licenses.Select(x => x.PackageID).Distinct();
            }

            await steam3.RequestPackageInfo(licenseQuery);

            foreach (var license in licenseQuery)
            {
                if (steam3.PackageInfo.TryGetValue(license, out var package) && package != null)
                {
                    if (package.KeyValues["appids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;

                    if (package.KeyValues["depotids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
                        return true;
                }
            }

            // Check if this app is free to download without a license
            var info = GetSteam3AppSection(appId, EAppInfoSection.Common);
            if (info != null && info["FreeToDownload"].AsBoolean())
                return true;

            return false;
        }

        internal static KeyValue GetSteam3AppSection(uint appId, EAppInfoSection section)
        {
            if (steam3 == null || steam3.AppInfo == null)
            {
                return null;
            }

            if (!steam3.AppInfo.TryGetValue(appId, out var app) || app == null)
            {
                return null;
            }

            var appinfo = app.KeyValues;
            var section_key = section switch
            {
                EAppInfoSection.Common => "common",
                EAppInfoSection.Extended => "extended",
                EAppInfoSection.Config => "config",
                EAppInfoSection.Depots => "depots",
                _ => throw new NotImplementedException(),
            };
            var section_kv = appinfo.Children.Where(c => c.Name == section_key).FirstOrDefault();
            return section_kv;
        }

        static uint GetSteam3AppBuildNumber(uint appId, string branch)
        {
            if (appId == INVALID_APP_ID)
                return 0;


            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (depots == null) return 0; // Mod for force download
            var branches = depots["branches"];
            var node = branches[branch];

            if (node == KeyValue.Invalid)
                return 0;

            var buildid = node["buildid"];

            if (buildid == KeyValue.Invalid)
                return 0;

            return uint.Parse(buildid.Value);
        }

        static uint GetSteam3DepotProxyAppId(uint depotId, uint appId)
        {
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            if (depots == null) return INVALID_APP_ID; // Mod for force download
            var depotChild = depots[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_APP_ID;

            if (depotChild["depotfromapp"] == KeyValue.Invalid)
                return INVALID_APP_ID;

            return depotChild["depotfromapp"].AsUnsignedInteger();
        }

        static async Task<ulong> GetSteam3DepotManifest(uint depotId, uint appId, string branch)
        {
            var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
            var depotChild = depots[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_MANIFEST_ID;

            // Shared depots can either provide manifests, or leave you relying on their parent app.
            // It seems that with the latter, "sharedinstall" will exist (and equals 2 in the one existance I know of).
            // Rather than relay on the unknown sharedinstall key, just look for manifests. Test cases: 111710, 346680.
            if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
            {
                var otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
                if (otherAppId == appId)
                {
                    // This shouldn't ever happen, but ya never know with Valve. Don't infinite loop.
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("App {0}, Depot {1} has depotfromapp of {2}!",
                        appId, depotId, otherAppId);
                    return INVALID_MANIFEST_ID;
                }

                await steam3.RequestAppInfo(otherAppId);

                return await GetSteam3DepotManifest(depotId, otherAppId, branch);
            }

            var manifests = depotChild["manifests"];

            if (manifests.Children.Count == 0)
                return INVALID_MANIFEST_ID;

            var node = manifests[branch]["gid"];

            // Non passworded branch, found the manifest
            if (node.Value != null)
                return ulong.Parse(node.Value);

            // If we requested public branch and it had no manifest, nothing to do
            if (string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
                return INVALID_MANIFEST_ID;

            // Either the branch just doesn't exist, or it has a password
            if (string.IsNullOrEmpty(Config.BetaPassword))
            {
                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine($"Branch {branch} for depot {depotId} was not found, either it does not exist or it has a password.");
                return INVALID_MANIFEST_ID;
            }

            if (!steam3.AppBetaPasswords.ContainsKey(branch))
            {
                // Submit the password to Steam now to get encryption keys
                await steam3.CheckAppBetaPassword(appId, Config.BetaPassword);

                if (!steam3.AppBetaPasswords.ContainsKey(branch))
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine($"Error: Password was invalid for branch {branch} (or the branch does not exist)");
                    return INVALID_MANIFEST_ID;
                }
            }

            // Got the password, request private depot section
            // TODO: We're probably repeating this request for every depot?
            var privateDepotSection = await steam3.GetPrivateBetaDepotSection(appId, branch);

            // Now repeat the same code to get the manifest gid from depot section
            depotChild = privateDepotSection[depotId.ToString()];

            if (depotChild == KeyValue.Invalid)
                return INVALID_MANIFEST_ID;

            manifests = depotChild["manifests"];

            if (manifests.Children.Count == 0)
                return INVALID_MANIFEST_ID;

            node = manifests[branch]["gid"];

            if (node.Value == null)
                return INVALID_MANIFEST_ID;

            return ulong.Parse(node.Value);
        }

        static string GetAppName(uint appId)
        {
            var info = GetSteam3AppSection(appId, EAppInfoSection.Common);
            if (info == null)
                return string.Empty;

            return info["name"].AsString();
        }

        static string GetAppInstallDir(uint appId)
        {
            var config = GetSteam3AppSection(appId, EAppInfoSection.Config);
            if (config != null)
            {
                var installDir = config["installdir"].AsString();
                if (!string.IsNullOrWhiteSpace(installDir))
                {
                    return installDir;
                }
            }

            var appName = GetAppName(appId);
            return string.IsNullOrWhiteSpace(appName) ? appId.ToString() : appName;
        }

        static void WriteAppManifest(uint appId, string branch, string language, IReadOnlyCollection<DepotDownloadInfo> depots, string configPath, uint stateFlags)
        {
            if (depots.Count == 0)
            {
                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("No depots resolved; skipping appmanifest generation.");
                return;
            }

            var manifestPath = Config.AppManifestFile;
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                // Steam-layout case: configPath is "steamapps"; .acf lives at the library root
                // (sibling of common/), matching Steam's own appmanifest_<id>.acf placement.
                // For -dir mode (configPath == InstallDirectory), keep the historical nested
                // path inside .DepotDownloader/ — preserves backward compatibility.
                if (string.Equals(configPath, STEAMAPPS_DIR, StringComparison.Ordinal))
                {
                    manifestPath = Path.Combine(configPath, $"appmanifest_{appId}.acf");
                }
                else
                {
                    manifestPath = Path.Combine(configPath, CONFIG_DIR, $"appmanifest_{appId}.acf");
                }
            }

            var manifest = new SteamAppManifest(
                appId,
                GetAppName(appId),
                GetAppInstallDir(appId),
                GetSteam3AppBuildNumber(appId, branch),
                language,
                stateFlags,
                depots.Select(depot => new SteamAppManifestDepot(depot.DepotId, depot.ManifestId, depot.SizeOnDisk)).ToList());

            AppManifestWriter.WriteToFile(manifestPath, manifest);
            (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Generated appmanifest metadata file: {0}", manifestPath);
            JsonProgressLogger.EmitAcfWritten(manifestPath);
        }

        public static bool InitializeSteam3(string username, string password)
        {
            string loginToken = null;

            if (username != null && Config.RememberPassword)
            {
                _ = AccountSettingsStore.Instance.LoginTokens.TryGetValue(username, out loginToken);
            }

            steam3 = new Steam3Session(
                new SteamUser.LogOnDetails
                {
                    Username = username,
                    Password = loginToken == null ? password : null,
                    ShouldRememberPassword = Config.RememberPassword,
                    AccessToken = loginToken,
                    LoginID = Config.LoginID ?? 0x534B32, // "SK2"
                }
            );

            if (!steam3.WaitForCredentials())
            {
                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Unable to get steam3 credentials.");
                return false;
            }

            Task.Run(steam3.TickCallbacks);

            return true;
        }

        public static void ShutdownSteam3()
        {
            if (steam3 == null)
                return;

            steam3.Disconnect();
        }
        private static async Task ProcessPublishedFileAsync(uint appId, ulong publishedFileId, List<ValueTuple<string, string>> fileUrls, List<ulong> contentFileIds)
        {
            var details = await steam3.GetPublishedFileDetails(appId, publishedFileId);
            var fileType = (EWorkshopFileType)details.file_type;

            if (fileType == EWorkshopFileType.Collection)
            {
                foreach (var child in details.children)
                {
                    await ProcessPublishedFileAsync(appId, child.publishedfileid, fileUrls, contentFileIds);
                }
            }
            else if (SupportedWorkshopFileTypes.Contains(fileType))
            {
                if (!string.IsNullOrEmpty(details?.file_url))
                {
                    fileUrls.Add((details.filename, details.file_url));
                }
                else if (details?.hcontent_file > 0)
                {
                    contentFileIds.Add(details.hcontent_file);
                }
                else
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Unable to locate manifest ID for published file {0}", publishedFileId);
                }
            }
            else
            {
                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Published file {0} has unsupported file type {1}. Skipping file", publishedFileId, fileType);
            }
        }

        public static async Task DownloadPubfileAsync(uint appId, ulong publishedFileId)
        {
            List<ValueTuple<string, string>> fileUrls = new();
            List<ulong> contentFileIds = new();

            await ProcessPublishedFileAsync(appId, publishedFileId, fileUrls, contentFileIds);

            foreach (var item in fileUrls)
            {
                await DownloadWebFile(appId, item.Item1, item.Item2);
            }

            if (contentFileIds.Count > 0)
            {
                var depotManifestIds = contentFileIds.Select(id => (appId, id)).ToList();
                await DownloadAppAsync(appId, depotManifestIds, DEFAULT_BRANCH, null, null, null, false, true);
            }
        }

        public static async Task DownloadUGCAsync(uint appId, ulong ugcId)
        {
            SteamCloud.UGCDetailsCallback details = null;

            if (steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser)
            {
                details = await steam3.GetUGCDetails(ugcId);
            }
            else
            {
                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine($"Unable to query UGC details for {ugcId} from an anonymous account");
            }

            if (!string.IsNullOrEmpty(details?.URL))
            {
                await DownloadWebFile(appId, details.FileName, details.URL);
            }
            else
            {
                await DownloadAppAsync(appId, [(appId, ugcId)], DEFAULT_BRANCH, null, null, null, false, true);
            }
        }

        private static async Task DownloadWebFile(uint appId, string fileName, string url)
        {
            if (!CreateDirectories(appId, 0, out var installDir))
            {
                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Error: Unable to create install directories!");
                return;
            }

            var stagingDir = Path.Combine(installDir, STAGING_DIR);
            var fileStagingPath = Path.Combine(stagingDir, fileName);
            var fileFinalPath = Path.Combine(installDir, fileName);

            Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath));
            Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath));

            using (var file = File.OpenWrite(fileStagingPath))
            using (var client = HttpClientFactory.CreateHttpClient())
            {
                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Downloading {0}", fileName);
                var responseStream = await client.GetStreamAsync(url);
                await responseStream.CopyToAsync(file);
            }

            if (File.Exists(fileFinalPath))
            {
                File.Delete(fileFinalPath);
            }

            File.Move(fileStagingPath, fileFinalPath);
        }

        public static async Task DownloadAppAsync(uint appId, List<(uint depotId, ulong manifestId)> depotManifestIds, string branch, string os, string arch, string language, bool lv, bool isUgc)
        {
            JsonProgressLogger.EmitAppStart(appId, depotManifestIds.Select(x => x.depotId));
            var depotsOk = new List<uint>();
            var depotsFailed = new List<uint>();
            var appSuccess = false;
            try
            {
                cdnPool = new CDNClientPool(steam3, appId);

                await steam3?.RequestAppInfo(appId);

                // Activate Steam-style library layout when the user didn't pin a -dir,
                // isn't doing UGC, and didn't pass an explicit -depot. In that case all
                // depots of the app get merged into ./steamapps/common/<installdir>/ and
                // the .acf state file lives alongside, matching Steam's on-disk convention.
                var steamLayoutActive = string.IsNullOrWhiteSpace(Config.InstallDirectory)
                                        && !isUgc
                                        && !Config.HasExplicitDepots;
                if (steamLayoutActive)
                {
                    Config.InstallDirectory = Path.Combine(STEAMAPPS_DIR, "common", GetAppInstallDir(appId));
                }

                // Load our configuration data containing the depots currently installed
                var configPath = Config.InstallDirectory;
                if (string.IsNullOrWhiteSpace(configPath))
                {
                    configPath = DEFAULT_DOWNLOAD_DIR;
                }
                if (steamLayoutActive)
                {
                    // Library-level state root: depot.config and appmanifest_<id>.acf live at
                    // ./steamapps/, not nested inside ./steamapps/common/<installdir>/.
                    configPath = STEAMAPPS_DIR;
                }

                // Local helpers — declared after configPath is finalized so the path
                // resolver can capture it. C# local functions require their captured
                // locals to be declared earlier in textual order (CS0841).
                bool ShouldWriteAppManifest() => steamLayoutActive || Config.GenerateAppManifest;

                string ResolveAppManifestPath()
                {
                    if (!string.IsNullOrWhiteSpace(Config.AppManifestFile))
                    {
                        return Config.AppManifestFile;
                    }
                    if (string.Equals(configPath, STEAMAPPS_DIR, StringComparison.Ordinal))
                    {
                        return Path.Combine(configPath, $"appmanifest_{appId}.acf");
                    }
                    return Path.Combine(configPath, CONFIG_DIR, $"appmanifest_{appId}.acf");
                }

                Directory.CreateDirectory(Path.Combine(configPath, CONFIG_DIR));
                DepotConfigStore.LoadFromFile(Path.Combine(configPath, CONFIG_DIR, "depot.config"));
                /*
                if (!await AccountHasAccess(appId))
                {
                    if (steam3.steamUser.SteamID.AccountType != EAccountType.AnonUser && await steam3.RequestFreeAppLicense(appId))
                    {
                        Console.WriteLine("Obtained FreeOnDemand license for app {0}", appId);

                        // Fetch app info again in case we didn't get it fully without a license.
                        await steam3.RequestAppInfo(appId, true);
                    }
                    else
                    {
                        var contentName = GetAppName(appId);
                        throw new ContentDownloaderException(string.Format("App {0} ({1}) is not available from this account.", appId, contentName));
                    }
                }
                */
                var hasSpecificDepots = depotManifestIds.Count > 0;
                var depotIdsFound = new List<uint>();
                var depotIdsExpected = depotManifestIds.Select(x => x.depotId).ToList();
                var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

                // Hoist the appmanifest.acf read up here (the full resume decision tree
                // below still uses these locals, but `skipInteractivePrompts` needs
                // `installed.StateFlags` *before* the depot-enumeration loop runs so
                // the platform / main-depot / DLC prompts can affect it.
                var acfPath = ResolveAppManifestPath();
                var installed = AppManifestReader.TryReadFromFile(acfPath);
                var appName = GetAppName(appId);
                var common = GetSteam3AppSection(appId, EAppInfoSection.Common);

                var skipInteractivePrompts =
                    installed != null && installed.StateFlags != InstalledAppManifest.StateFullyInstalled;

                // Tracks whether the user actually opted into platform filtering this run
                // (either via the interactive prompt below, or via a CLI platform flag).
                // The Lua-batch platform filter at the bottom of this method gates on this
                // so we don't silently prune Lua depots against host defaults when the user
                // never picked a platform (e.g. non-TTY runs without -os/-osarch/-language).
                var platformPromptRan = false;

                // Platform prompt — only when steamLayoutActive, TTY, no platform CLI flag,
                // not in resume-from-interrupted path, no saved choice for this app yet,
                // AND at least one axis has >= 2 distinct values.
                if (steamLayoutActive
                    && Ansi.CanUseInteractiveProgress
                    && !Config.HasExplicitPlatformArgs
                    && !skipInteractivePrompts
                    && depots != null
                    && !(DepotConfigStore.Instance?.AppConfigs.ContainsKey(appId) ?? false))
                {
                    var platformSel = AppSelectionPrompt.PromptPlatform(depots, common);

                    if (platformSel.AllPlatforms)
                    {
                        Config.DownloadAllPlatforms = true;
                    }
                    else if (platformSel.Os != null)
                    {
                        os = platformSel.Os;
                    }

                    if (platformSel.AllArchs)
                    {
                        Config.DownloadAllArchs = true;
                    }
                    else if (platformSel.Arch != null)
                    {
                        arch = platformSel.Arch;
                    }

                    if (platformSel.AllLanguages)
                    {
                        Config.DownloadAllLanguages = true;
                    }
                    else if (platformSel.Language != null)
                    {
                        language = platformSel.Language;
                    }

                    platformPromptRan = true;
                }

                // Restore prior platform choice on resume / second run when the user
                // didn't override via CLI and the interactive prompt didn't fire.
                // Setting platformPromptRan = true propagates the choice to the
                // Lua-batch post-resolution filter (which gates on it).
                if (!Config.HasExplicitPlatformArgs
                    && !platformPromptRan
                    && DepotConfigStore.Instance != null
                    && DepotConfigStore.Instance.AppConfigs.TryGetValue(appId, out var savedAppCfg))
                {
                    // Saved non-null value wins; saved null means "no filter on this axis"
                    // (all-platforms / all-archs / all-languages).
                    static void RestoreAxis(string saved, ref string target, Action setAll)
                    {
                        if (saved != null) target = saved;
                        else setAll();
                    }

                    RestoreAxis(savedAppCfg.Os, ref os, () => Config.DownloadAllPlatforms = true);
                    RestoreAxis(savedAppCfg.Arch, ref arch, () => Config.DownloadAllArchs = true);
                    RestoreAxis(savedAppCfg.Language, ref language, () => Config.DownloadAllLanguages = true);

                    platformPromptRan = true;
                }

                // Persist this run's platform decision (from prompt, CLI, or restored
                // saved state). Idempotent — restoring then re-saving writes back the
                // same bytes; an interactive override or new CLI flag overwrites the
                // prior entry. Skip UGC paths: they never set os/arch/language and
                // would write {null,null,null}, which a future non-UGC run would
                // misread as "all platforms" and skip the prompt. Inverse of the
                // RestoreAxis contract above: null on an axis means "no filter".
                if (!isUgc && DepotConfigStore.Instance != null)
                {
                    static string PersistAxis(bool all, string current) => all ? null : current;

                    DepotConfigStore.Instance.AppConfigs[appId] = new AppDownloadConfig
                    {
                        Os = PersistAxis(Config.DownloadAllPlatforms, os),
                        Arch = PersistAxis(Config.DownloadAllArchs, arch),
                        Language = PersistAxis(Config.DownloadAllLanguages, language),
                    };
                    DepotConfigStore.Save();
                }

                var dlcCandidates = new List<uint>();
                if (Config.BatchLuaDownload && Config.LuaDeclaredApps != null)
                {
                    var extended = GetSteam3AppSection(appId, EAppInfoSection.Extended);
                    dlcCandidates = AppSelectionPrompt.ComputeDlcCandidates(
                        Config.LuaDeclaredApps,
                        appId,
                        depots,
                        extended);
                }

                // Main-depot prompt — advanced opt-in via [y/N], when multiple non-shared
                // main depots remain after the platform filter, or when DLCs are selectable.
                // Skip when the user named
                // depots explicitly via -depot: the prompt would enumerate ALL PICS depots
                // (not just user-named ones), and a follow-up prune at the end of the
                // depot loop would silently drop CLI-named depots the user did request.
                var deselectedMainDepots = new HashSet<uint>();
                if (steamLayoutActive
                    && Ansi.CanUseInteractiveProgress
                    && !skipInteractivePrompts
                    && !Config.HasExplicitDepots
                    && depots != null)
                {
                    var mainCandidates = AppSelectionPrompt.ComputeMainDepotCandidates(
                        depots, appId,
                        os: os ?? Util.GetSteamOS(),
                        allPlatforms: Config.DownloadAllPlatforms,
                        arch: arch ?? Util.GetSteamArch(),
                        allArchs: Config.DownloadAllArchs,
                        language: language ?? "english",
                        allLanguages: Config.DownloadAllLanguages);

                    if (AppSelectionPrompt.ShouldPromptMainDepots(mainCandidates.Count, dlcCandidates.Count > 0))
                    {
                        var selected = AppSelectionPrompt.PromptMainDepots(mainCandidates, depots);
                        var selectedSet = new HashSet<uint>(selected);
                        foreach (var id in mainCandidates)
                        {
                            if (!selectedSet.Contains(id))
                            {
                                deselectedMainDepots.Add(id);
                            }
                        }
                    }
                }

                // DLC prompt — only in Lua batch mode when >= 1 declared DLC app id (in
                // LuaOwnedApps minus the main appId).
                if (steamLayoutActive
                    && Ansi.CanUseInteractiveProgress
                    && !skipInteractivePrompts
                    && Config.BatchLuaDownload
                    && Config.LuaDeclaredApps != null)
                {
                    if (dlcCandidates.Count > 0)
                    {
                        // Batch-prefetch PICS for each DLC app so the prompt can show
                        // friendly names (falls back to bare app id if denied).
                        await steam3.RequestAppInfoMany(dlcCandidates);

                        var selected = AppSelectionPrompt.PromptDlcs(
                            dlcCandidates,
                            id => GetAppName(id));
                        var selectedSet = new HashSet<uint>(selected);

                        foreach (var dlcId in dlcCandidates)
                        {
                            if (!selectedSet.Contains(dlcId))
                            {
                                // Drop from all four locations: Lua manifest map, token map,
                                // owned-apps set, and the working depot list. The DLC's
                                // depot id usually equals its app id, but Steam PICS can also
                                // identify the owning DLC through depots.<id>.dlcappid.
                                Config.LuaManifestIds?.Remove(dlcId);
                                Config.LuaAppTokens?.Remove(dlcId);
                                Config.LuaDeclaredApps.Remove(dlcId);
                                var dlcAppDepots = KeyValue.Invalid;
                                if (steam3.AppInfo.TryGetValue(dlcId, out var dlcAppInfo) && dlcAppInfo != null)
                                {
                                    dlcAppDepots = dlcAppInfo.KeyValues["depots"];
                                }
                                depotManifestIds.RemoveAll(entry =>
                                    AppSelectionPrompt.DepotBelongsToDlc(entry.depotId, dlcId, depots, dlcAppDepots));
                            }
                        }
                    }
                }

                if (isUgc)
                {
                    var workshopDepot = depots["workshopdepot"].AsUnsignedInteger();
                    if (workshopDepot != 0 && !depotIdsExpected.Contains(workshopDepot))
                    {
                        depotIdsExpected.Add(workshopDepot);
                        depotManifestIds = depotManifestIds.Select(pair => (workshopDepot, pair.manifestId)).ToList();
                    }

                    depotIdsFound.AddRange(depotIdsExpected);
                }
                else
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Using app branch: '{0}'.", branch);

                    if (depots != null)
                    {
                        foreach (var depotSection in depots.Children)
                        {
                            var id = INVALID_DEPOT_ID;
                            if (depotSection.Children.Count == 0)
                                continue;

                            if (!uint.TryParse(depotSection.Name, out id))
                                continue;

                            if (hasSpecificDepots && !depotIdsExpected.Contains(id))
                                continue;

                            // Apply main-depot deselection (set by the Step 3 prompt).
                            if (deselectedMainDepots.Contains(id))
                            {
                                continue;
                            }

                            if (!hasSpecificDepots)
                            {
                                var depotConfig = depotSection["config"];
                                if (!AppSelectionPrompt.DepotMatchesPlatform(
                                        depotConfig,
                                        os ?? Util.GetSteamOS(), Config.DownloadAllPlatforms,
                                        arch ?? Util.GetSteamArch(), Config.DownloadAllArchs,
                                        language ?? "english", Config.DownloadAllLanguages))
                                {
                                    continue;
                                }

                                if (depotConfig != KeyValue.Invalid &&
                                    !lv &&
                                    depotConfig["lowviolence"] != KeyValue.Invalid &&
                                    depotConfig["lowviolence"].AsBoolean())
                                {
                                    continue;
                                }
                            }

                            depotIdsFound.Add(id);

                            if (!hasSpecificDepots)
                                depotManifestIds.Add((id, INVALID_MANIFEST_ID));
                        }
                    }

                    // Drop deselected main depots from the working list — in non-Lua
                    // mode they were never added by the loop above (which skips them
                    // via the new `deselectedMainDepots.Contains(id)` continue); in
                    // Lua batch mode they're in `depotManifestIds` from Program.cs.
                    AppSelectionPrompt.RemoveDeselectedMainDepots(depotManifestIds, deselectedMainDepots);

                    if (depotManifestIds.Count == 0 && !hasSpecificDepots)
                    {
                        throw new ContentDownloaderException(string.Format("Couldn't find any depots to download for app {0}", appId));
                    }

                    if (depotIdsFound.Count < depotIdsExpected.Count)
                    {
                        var remainingDepotIds = depotIdsExpected.Except(depotIdsFound);
                        //throw new ContentDownloaderException(string.Format("Depot {0} not listed for app {1}", string.Join(", ", remainingDepotIds), appId));
                        // Mod for force download
                    }
                }

                // When batch-downloading via Lua, skip depots that Steam PICS marks as
                // shared installs (typical VC++ / DirectX redists from app 228980) so
                // they do not appear as selectable DLC content.
                if (Config.BatchLuaDownload && !isUgc && depots != null)
                {
                    depotManifestIds = depotManifestIds.Where(entry =>
                    {
                        var depotChild = depots[entry.depotId.ToString()];
                        if (depotChild == KeyValue.Invalid) return true;
                        if (AppSelectionPrompt.IsSharedDepot(depotChild, appId))
                        {
                            var fromApp = depotChild["depotfromapp"].AsUnsignedInteger();
                            if (fromApp != 0)
                            {
                                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Skipping shared depot {0} (provided by app {1})", entry.depotId, fromApp);
                            }
                            else
                            {
                                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Skipping shared depot {0} (sharedinstall)", entry.depotId);
                            }
                            return false;
                        }
                        return true;
                    }).ToList();
                }

                var appDepotsByAppId = new Dictionary<uint, KeyValue>();
                if (Config.BatchLuaDownload && !isUgc && Config.LuaDeclaredApps != null)
                {
                    var appInfoIds = Config.LuaDeclaredApps
                        .Where(id => id != appId && !steam3.AppInfo.ContainsKey(id))
                        .Distinct()
                        .ToList();

                    if (appInfoIds.Count > 0)
                    {
                        await steam3.RequestAppInfoMany(appInfoIds);
                    }

                    foreach (var luaAppId in Config.LuaDeclaredApps)
                    {
                        if (luaAppId == appId)
                        {
                            continue;
                        }

                        if (steam3.AppInfo.TryGetValue(luaAppId, out var appInfo) && appInfo != null)
                        {
                            var appDepots = appInfo.KeyValues["depots"];
                            if (appDepots != KeyValue.Invalid)
                            {
                                appDepotsByAppId[luaAppId] = appDepots;
                            }
                        }
                    }
                }

                // Extend platform filter to Lua-batch depots. Developer mode (explicit
                // -depot but no Lua batch) is intentionally left alone — user named the
                // depots, don't second-guess. Also skip when the user never opted into
                // platform filtering (no CLI flag, no interactive prompt) — otherwise we
                // would prune Lua depots against host defaults the user never confirmed.
                if (steamLayoutActive
                    && Config.BatchLuaDownload
                    && (Config.HasExplicitPlatformArgs || platformPromptRan)
                    && (!Config.DownloadAllPlatforms || !Config.DownloadAllArchs || !Config.DownloadAllLanguages))
                {
                    var resolvedOs = os ?? Util.GetSteamOS();
                    var resolvedArch = arch ?? Util.GetSteamArch();
                    var resolvedLanguage = language ?? "english";

                    depotManifestIds = depotManifestIds.Where(entry =>
                    {
                        var depotId = entry.depotId;
                        if (!AppSelectionPrompt.TryResolveDepotOwnerAppId(
                                depotId,
                                appId,
                                depots,
                                appDepotsByAppId,
                                out _,
                                out var depotSection))
                        {
                            return true;
                        }

                        return AppSelectionPrompt.DepotMatchesPlatform(
                            depotSection["config"],
                            resolvedOs, Config.DownloadAllPlatforms,
                            resolvedArch, Config.DownloadAllArchs,
                            resolvedLanguage, Config.DownloadAllLanguages);
                    }).ToList();
                }

                var infos = new List<DepotDownloadInfo>();

                foreach (var (depotId, manifestId) in depotManifestIds)
                {
                    var ownerAppId = appId;
                    if (Config.BatchLuaDownload && !isUgc && !AppSelectionPrompt.TryResolveDepotOwnerAppId(
                            depotId,
                            appId,
                            depots,
                            appDepotsByAppId,
                            out ownerAppId,
                            out _))
                    {
                        (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Skipping depot {0}: not found in app {1} or declared Lua DLC app depots.", depotId, appId);
                        continue;
                    }

                    var info = await GetDepotInfo(depotId, ownerAppId, manifestId, branch);
                    if (info != null)
                    {
                        infos.Add(info);
                    }
                }

                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine();

                // `acfPath`, `installed`, and `appName` are declared earlier (hoisted
                // before the prompt blocks so `skipInteractivePrompts` could gate them).
                if (installed == null)
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Installing app {0} ({1})...", appId, appName);
                }
                else if (installed.StateFlags == InstalledAppManifest.StateFullyInstalled)
                {
                    var mismatched = 0;
                    foreach (var (depotId, manifestId) in infos.Select(d => (d.DepotId, d.ManifestId)))
                    {
                        installed.InstalledDepots.TryGetValue(depotId, out var recorded);
                        if (ShouldLogInstalledManifestComparison(DebugLog.Enabled))
                        {
                            DebugLog.WriteLine(nameof(ContentDownloader), GetInstalledManifestComparisonMessage(depotId, recorded, manifestId));
                        }

                        if (recorded != manifestId)
                        {
                            mismatched++;
                        }
                    }
                    if (ShouldSkipFullyInstalledApp(
                        installed.InstalledDepots,
                        infos.Select(d => (d.DepotId, d.ManifestId)),
                        Config.VerifyAll))
                    {
                        (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("App {0} ({1}) is fully installed (build {2}). Nothing to do.", appId, appName, installed.BuildId);
                        appSuccess = true;  // Already fully installed counts as success.
                        return;
                    }
                    if (mismatched > 0)
                    {
                        (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Update available for app {0} ({1}): {2} depot(s) have new manifests. Proceeding...", appId, appName, mismatched);
                    }
                    else if (Config.VerifyAll)
                    {
                        (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("App {0} ({1}) is fully installed; re-verifying because -verify-all was passed.", appId, appName);
                    }
                }
                else // StateFlags != StateFullyInstalled (resume-from-interrupted path)
                {
                    var completed = 0;
                    foreach (var (depotId, manifestId) in infos.Select(d => (d.DepotId, d.ManifestId)))
                    {
                        installed.InstalledDepots.TryGetValue(depotId, out var recorded);
                        if (recorded == manifestId && recorded != 0)
                        {
                            completed++;
                        }
                    }
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Previous download was interrupted (StateFlags={0}). {1}/{2} depots already installed, resuming...", installed.StateFlags, completed, infos.Count);
                }

                // Detect any depot-mode installs of the requested depots and offer to migrate
                // their files into the Steam-layout location. Only fires in steam-layout mode
                // (Config.InstallDirectory is the steamapps/common/<installdir>/ path by this
                // point, set by the steamLayoutActive override earlier in this method).
                if (steamLayoutActive)
                {
                    DepotMigration.MaybeMigrate(
                        infos.Select(d => (d.DepotId, d.ManifestId)),
                        Config.InstallDirectory,
                        autoMigrate: Config.MigrateDepotInstalls,
                        interactive: Ansi.CanUseInteractiveProgress);
                }

                if (ShouldWriteAppManifest())
                {
                    WriteAppManifest(appId, branch, language, infos, configPath, stateFlags: 1026);
                }

                try
                {
                    await DownloadSteam3Async(infos, depotsOk, depotsFailed).ConfigureAwait(false);

                    var downloadSucceeded = depotsFailed.Count == 0 && depotsOk.Count > 0;

                    if (ShouldWriteAppManifest())
                    {
                        WriteAppManifest(appId, branch, language, infos, configPath, stateFlags: 4);
                    }

                    appSuccess = downloadSucceeded;

                    if (downloadSucceeded && !Config.DownloadManifestOnly)
                    {
                        try
                        {
                            await SteamlessIntegration.TryRunAsync(
                                Config.InstallDirectory,
                                logLine: WritePostProcessLog).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            WritePostProcessLog($"Steamless warning: post-download patch failed: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("App {0} was not completely downloaded.", appId);
                    throw;
                }
            }
            finally
            {
                JsonProgressLogger.EmitAppDone(appSuccess, depotsOk, depotsFailed);
            }
        }

        static void WritePostProcessLog(string message)
        {
            (Config.JsonProgress ? Console.Error : Console.Out).WriteLine(message);
        }

        static async Task<DepotDownloadInfo> GetDepotInfo(uint depotId, uint appId, ulong manifestId, string branch)
        {
            if (steam3 != null && appId != INVALID_APP_ID)
            {
                await steam3.RequestAppInfo(appId);
            }

            /*
            if (!await AccountHasAccess(depotId))
            {
                Console.WriteLine("Depot {0} is not available from this account.", depotId);

                return null;
            }
            */

            if (manifestId == INVALID_MANIFEST_ID)
            {
                manifestId = await GetSteam3DepotManifest(depotId, appId, branch);
                if (manifestId == INVALID_MANIFEST_ID && !string.Equals(branch, DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Warning: Depot {0} does not have branch named \"{1}\". Trying {2} branch.", depotId, branch, DEFAULT_BRANCH);
                    branch = DEFAULT_BRANCH;
                    manifestId = await GetSteam3DepotManifest(depotId, appId, branch);
                }

                if (manifestId == INVALID_MANIFEST_ID)
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Depot {0} missing public subsection or manifest section.", depotId);
                    return null;
                }

                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine(GetResolvedLatestManifestMessage(depotId, appId, branch, manifestId));
            }

            byte[] depotKey = null;
            if (DepotKeyStore.ContainsKey(depotId))
            {
                depotKey = DepotKeyStore.Get(depotId);
                steam3.DepotKeys.Add(depotId, depotKey);
            }
            else
            {
                await steam3.RequestDepotKey(depotId, appId);
            }
            if (!steam3.DepotKeys.TryGetValue(depotId, out depotKey))
            {
                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("No valid depot key for {0}, unable to download.", depotId);
                return null;
            }

            var uVersion = GetSteam3AppBuildNumber(appId, branch);

            if (!CreateDirectories(depotId, uVersion, out var installDir))
            {
                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Error: Unable to create install directories!");
                return null;
            }

            // For depots that are proxied through depotfromapp, we still need to resolve the proxy app id, unless the app is freetodownload
            var containingAppId = appId;
            var proxyAppId = GetSteam3DepotProxyAppId(depotId, appId);
            if (proxyAppId != INVALID_APP_ID)
            {
                var common = GetSteam3AppSection(appId, EAppInfoSection.Common);
                if (common == null || !common["FreeToDownload"].AsBoolean())
                {
                    containingAppId = proxyAppId;
                }
            }

            return new DepotDownloadInfo(depotId, containingAppId, manifestId, branch, installDir, depotKey);
        }

        private class ChunkMatch(DepotManifest.ChunkData oldChunk, DepotManifest.ChunkData newChunk)
        {
            public DepotManifest.ChunkData OldChunk { get; } = oldChunk;
            public DepotManifest.ChunkData NewChunk { get; } = newChunk;
        }

        private class DepotFilesData
        {
            public DepotDownloadInfo depotDownloadInfo;
            public DepotDownloadCounter depotCounter;
            public string stagingDir;
            public ResumeStateStore resumeStateStore;
            public DepotManifest manifest;
            public DepotManifest previousManifest;
            public List<DepotManifest.FileData> filteredFiles;
            public HashSet<string> allFileNames;
        }

        private class FileStreamData
        {
            public FileStream fileStream;
            public SemaphoreSlim fileLock;
            public int chunksToDownload;
        }

        private static void CreditCompletedChunk(
            GlobalDownloadCounter downloadCounter,
            DepotDownloadCounter depotDownloadCounter,
            DepotManifest.ChunkData chunk)
        {
            lock (depotDownloadCounter)
            {
                depotDownloadCounter.sizeDownloaded += chunk.UncompressedLength;
            }

            downloadCounter.AddCompletedBytes(chunk.UncompressedLength);
            downloadCounter.AddCompletedChunks(1);
        }

        internal static void RecordValidatedChunkCompleted(
            ResumeStateStore resumeStateStore,
            DepotManifest.FileData file,
            DepotManifest.ChunkData chunk,
            bool matched,
            GlobalDownloadCounter downloadCounter)
        {
            if (matched)
            {
                resumeStateStore?.MarkChunkCompleted(file, chunk, downloadCounter);
            }
        }

        internal static bool ShouldTreatExistingFileAsPreallocatedEmpty(
            ResumeStateStore resumeStateStore,
            DepotManifest.FileData file,
            FileInfo fileInfo)
        {
            return resumeStateStore?.CanUseForResume == true
                && !Config.VerifyAll
                && (ulong)fileInfo.Length == file.TotalSize
                && !resumeStateStore.State.HasCompletedChunks(file);
        }

        internal static bool ShouldLoadNewManifestFromDirectory(DownloadConfig config)
        {
            return config?.UseManifestDirectory == true && !config.IgnoreLuaManifestIds;
        }

        internal static string GetSkippedManifestDirectoryCacheMessage(uint depotId)
        {
            return string.Format("Skipping manifestdir manifest cache for depot {0} because -no-lua-mid is enabled.", depotId);
        }

        internal static string GetDownloadingManifestMessage(uint depotId, ulong manifestId)
        {
            return string.Format("Downloading depot {0} manifest {1} from Steam/CDN.", depotId, manifestId);
        }

        internal static string GetResolvedLatestManifestMessage(uint depotId, uint appId, string branch, ulong manifestId)
        {
            return string.Format("Resolved latest manifest for depot {0} from app {1} branch '{2}': {3}.", depotId, appId, branch, manifestId);
        }

        internal static string GetInstalledManifestComparisonMessage(uint depotId, ulong installedManifestId, ulong targetManifestId)
        {
            return string.Format("Appmanifest comparison for depot {0}: installed manifest {1}, target manifest {2}.", depotId, installedManifestId, targetManifestId);
        }

        internal static bool ShouldLogInstalledManifestComparison(bool debugEnabled)
        {
            return debugEnabled;
        }

        internal static bool ShouldSkipFullyInstalledApp(
            IReadOnlyDictionary<uint, ulong> installedDepots,
            IEnumerable<(uint depotId, ulong manifestId)> targetDepots,
            bool verifyAll)
        {
            if (verifyAll || installedDepots == null || targetDepots == null)
            {
                return false;
            }

            var anyTargetDepot = false;
            foreach (var (depotId, manifestId) in targetDepots)
            {
                anyTargetDepot = true;
                installedDepots.TryGetValue(depotId, out var installedManifestId);
                if (installedManifestId != manifestId)
                {
                    return false;
                }
            }

            return anyTargetDepot;
        }

        private class DepotDownloadCounter
        {
            public ulong completeDownloadSize;
            public ulong sizeDownloaded;
            public ulong depotBytesCompressed;
            public ulong depotBytesUncompressed;
            public int totalChunks;
            public int completedChunks;
        }

        private static async Task DownloadSteam3Async(List<DepotDownloadInfo> depots, List<uint> depotsOk, List<uint> depotsFailed)
        {
            Ansi.Progress(Ansi.ProgressState.Indeterminate);

            await cdnPool.UpdateServerList();

            var cts = new CancellationTokenSource();
            var downloadCounter = new GlobalDownloadCounter();
            var depotsToDownload = new List<DepotFilesData>(depots.Count);
            var allFileNamesAllDepots = new HashSet<string>();

            // First, fetch all the manifests for each depot (including previous manifests) and perform the initial setup
            foreach (var depot in depots)
            {
                var depotFileData = await ProcessDepotManifestAndFiles(cts, depot, downloadCounter);

                if (depotFileData != null)
                {
                    depotsToDownload.Add(depotFileData);
                    allFileNamesAllDepots.UnionWith(depotFileData.allFileNames);

                    // Emit depot_start once the manifest is fetched and the total
                    // uncompressed size is known.
                    JsonProgressLogger.EmitDepotStart(
                        depot.DepotId,
                        depot.ManifestId,
                        depotFileData.manifest?.TotalUncompressedSize);
                }

                cts.Token.ThrowIfCancellationRequested();
            }

            // If we're about to write all the files to the same directory, we will need to first de-duplicate any files by path
            // This is in last-depot-wins order, from Steam or the list of depots supplied by the user
            if (!string.IsNullOrWhiteSpace(Config.InstallDirectory) && depotsToDownload.Count > 0)
            {
                var claimedFileNames = new HashSet<string>();

                for (var i = depotsToDownload.Count - 1; i >= 0; i--)
                {
                    // For each depot, remove all files from the list that have been claimed by a later depot
                    depotsToDownload[i].filteredFiles.RemoveAll(file => claimedFileNames.Contains(file.FileName));

                    claimedFileNames.UnionWith(depotsToDownload[i].allFileNames);
                }
            }

            foreach (var depotFileData in depotsToDownload)
            {
                depotFileData.depotDownloadInfo.SizeOnDisk = depotFileData.filteredFiles
                    .Where(file => !file.Flags.HasFlag(EDepotFileFlag.Directory))
                    .Aggregate(0UL, (size, file) => size + file.TotalSize);
            }

            // Compute per-depot totalChunks AFTER any de-dup pruning above, so
            // the grand-total seed reflects the actual download set. Pure
            // arithmetic on already-decoded manifests — no I/O.
            foreach (var depotFileData in depotsToDownload)
            {
                var depot = depotFileData.depotDownloadInfo;
                depotFileData.resumeStateStore = ResumeStateStore.Open(
                    DepotConfigStore.ConfigDirectory,
                    depot.AppId,
                    depot.DepotId,
                    depot.ManifestId,
                    depot.InstallDir,
                    depotFileData.filteredFiles,
                    Config.VerifyAll,
                    downloadCounter);

                depotFileData.depotCounter.totalChunks = depotFileData.filteredFiles
                    .Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))
                    .Sum(f => f.Chunks.Count);

                foreach (var file in depotFileData.filteredFiles.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)))
                {
                    var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                    if (!File.Exists(fileFinalPath))
                    {
                        continue;
                    }

                    var oldManifestFile = depotFileData.previousManifest?.Files.SingleOrDefault(f => f.FileName == file.FileName);
                    var (verifyBytes, verifyChunks) = GetVerifyWorkForExistingFile(file, oldManifestFile, depotFileData.resumeStateStore, fileFinalPath);
                    downloadCounter.RegisterVerifyWork(verifyBytes, verifyChunks);
                }
            }

            var useInteractiveProgress = Ansi.CanUseInteractiveProgress && downloadCounter.completeDownloadSize > 0;
            var totalDownloadSize = downloadCounter.completeDownloadSize;

            // Sum chunk totals across all depots and seed the global counter
            // once, so the bar's C N/M denominator is final from the first
            // Progress frame instead of growing depot-by-depot.
            var grandTotalChunks = depotsToDownload.Sum(d => d.depotCounter.totalChunks);
            downloadCounter.RegisterDepotChunks(0, grandTotalChunks);

            try
            {
                if (useInteractiveProgress)
                {
                    downloadCounter.Begin(totalDownloadSize, true);

                    await Ansi.RunWithProgressAsync(downloadCounter, async () =>
                    {
                        foreach (var depotFileData in depotsToDownload)
                        {
                            await RunDepotDownloadAsync(cts, downloadCounter, depotFileData, allFileNamesAllDepots, depotsOk, depotsFailed);
                        }

                        downloadCounter.Finish();
                    });
                }
                else
                {
                    downloadCounter.Begin(totalDownloadSize, false);

                    foreach (var depotFileData in depotsToDownload)
                    {
                        await RunDepotDownloadAsync(cts, downloadCounter, depotFileData, allFileNamesAllDepots, depotsOk, depotsFailed);
                    }

                    downloadCounter.Finish();
                }
            }
            finally
            {
                Ansi.Progress(Ansi.ProgressState.Hidden);
            }

            (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Total downloaded: {0} bytes ({1} bytes uncompressed) from {2} depots",
                downloadCounter.totalBytesCompressed, downloadCounter.totalBytesUncompressed, depots.Count);
        }

        private static async Task<DepotFilesData> ProcessDepotManifestAndFiles(CancellationTokenSource cts, DepotDownloadInfo depot, GlobalDownloadCounter downloadCounter)
        {
            var depotCounter = new DepotDownloadCounter();

            (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Processing depot {0}", depot.DepotId);

            DepotManifest oldManifest = null;
            DepotManifest newManifest = null;
            var configDir = Path.Combine(depot.InstallDir, CONFIG_DIR);

            var lastManifestId = INVALID_MANIFEST_ID;
            DepotConfigStore.Instance.InstalledManifestIDs.TryGetValue(depot.DepotId, out lastManifestId);

            // In case we have an early exit, this will force equiv of verifyall next run.
            DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = INVALID_MANIFEST_ID;
            DepotConfigStore.Save();

            if (lastManifestId != INVALID_MANIFEST_ID)
            {
                // We only have to show this warning if the old manifest ID was different
                var badHashWarning = (lastManifestId != depot.ManifestId);
                oldManifest = Util.LoadManifestFromFile(configDir, depot.DepotId, lastManifestId, badHashWarning);

                // Fall back to the user-supplied manifest directory so previously-installed manifests
                // there are picked up as the "old" manifest and per-file FileHash comparison can short-circuit
                // re-validation when the file content hasn't changed.
                if (oldManifest == null && Config.UseManifestDirectory)
                {
                    oldManifest = Util.LoadManifestFromFile(Config.ManifestDirectory, depot.DepotId, lastManifestId, badHashWarning);
                }
            }

            if (Config.UseManifestFile)
            {
                newManifest = DepotManifest.LoadFromFile(Config.ManifestFile);

                if (newManifest == null)
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Unable to load manifest file {0} for depot {1}", Config.ManifestFile, depot.DepotId);
                    cts.Cancel();
                }
                else
                {
                    // Cache the manifest in configDir so subsequent runs can load it as the "old" manifest
                    // and short-circuit per-file validation when FileHash matches.
                    Util.SaveManifestToFile(configDir, newManifest);
                }
            }
            else if (Config.UseManifestDirectory && Config.IgnoreLuaManifestIds)
            {
                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine(GetSkippedManifestDirectoryCacheMessage(depot.DepotId));
            }

            if (newManifest == null && ShouldLoadNewManifestFromDirectory(Config))
            {
                newManifest = Util.LoadManifestFromFile(Config.ManifestDirectory, depot.DepotId, depot.ManifestId, true);

                if (newManifest == null)
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Unable to load manifest {0} for depot {1} from directory {2}", depot.ManifestId, depot.DepotId, Config.ManifestDirectory);
                    cts.Cancel();
                }
                else
                {
                    Util.SaveManifestToFile(configDir, newManifest);
                }
            }
            else if (newManifest == null && lastManifestId == depot.ManifestId && oldManifest != null)
            {
                newManifest = oldManifest;
                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Already have manifest {0} for depot {1}.", depot.ManifestId, depot.DepotId);
            }
            else if (newManifest == null)
            {
                newManifest = Util.LoadManifestFromFile(configDir, depot.DepotId, depot.ManifestId, true);

                if (newManifest != null)
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Already have manifest {0} for depot {1}.", depot.ManifestId, depot.DepotId);
                }
                else
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine(GetDownloadingManifestMessage(depot.DepotId, depot.ManifestId));

                    ulong manifestRequestCode = 0;
                    var manifestRequestCodeExpiration = DateTime.MinValue;

                    do
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        Server connection = null;

                        try
                        {
                            connection = cdnPool.GetConnection();

                            string cdnToken = null;
                            if (steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
                            {
                                var result = await authTokenCallbackPromise.Task;
                                cdnToken = result.Token;
                            }

                            var now = DateTime.Now;

                            // In order to download this manifest, we need the current manifest request code
                            // The manifest request code is only valid for a specific period in time
                            if (manifestRequestCode == 0 || now >= manifestRequestCodeExpiration)
                            {
                                manifestRequestCode = await steam3.GetDepotManifestRequestCodeAsync(
                                    depot.DepotId,
                                    depot.AppId,
                                    depot.ManifestId,
                                    depot.Branch);

                                manifestRequestCode = await ManifestRequestCodeProvider.GetWithFallbackAsync(
                                    manifestRequestCode,
                                    depot.ManifestId,
                                    ManifestRequestCodeProvider.GetFromGmrcAsync).ConfigureAwait(false);

                                // This code will hopefully be valid for one period following the issuing period
                                manifestRequestCodeExpiration = now.Add(TimeSpan.FromMinutes(5));

                                // If we could not get the manifest code, this is a fatal error
                                if (manifestRequestCode == 0)
                                {
                                    cts.Cancel();
                                }
                            }

                            DebugLog.WriteLine("ContentDownloader",
                                "Downloading manifest {0} from {1} with {2}",
                                depot.ManifestId,
                                connection,
                                cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                            newManifest = await cdnPool.CDNClient.DownloadManifestAsync(
                                depot.DepotId,
                                depot.ManifestId,
                                manifestRequestCode,
                                connection,
                                depot.DepotKey,
                                cdnPool.ProxyServer,
                                cdnToken).ConfigureAwait(false);

                            cdnPool.ReturnConnection(connection);
                        }
                        catch (TaskCanceledException)
                        {
                            (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Connection timeout downloading depot manifest {0} {1}. Retrying.", depot.DepotId, depot.ManifestId);
                        }
                        catch (SteamKitWebRequestException e)
                        {
                            // If the CDN returned 403, attempt to get a cdn auth if we didn't yet
                            if (e.StatusCode == HttpStatusCode.Forbidden && !steam3.CDNAuthTokens.ContainsKey((depot.DepotId, connection.Host)))
                            {
                                await steam3.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);

                                cdnPool.ReturnConnection(connection);

                                continue;
                            }

                            cdnPool.ReturnBrokenConnection(connection);

                            if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                            {
                                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Encountered {2} for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId, (int)e.StatusCode);
                                break;
                            }

                            if (e.StatusCode == HttpStatusCode.NotFound)
                            {
                                (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Encountered 404 for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId);
                                break;
                            }

                            (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Encountered error downloading depot manifest {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.StatusCode);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception e)
                        {
                            cdnPool.ReturnBrokenConnection(connection);
                            (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Encountered error downloading manifest for depot {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.Message);
                        }
                    } while (newManifest == null);

                    if (newManifest == null)
                    {
                        (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("\nUnable to download manifest {0} for depot {1}", depot.ManifestId, depot.DepotId);
                        cts.Cancel();
                    }

                    // Throw the cancellation exception if requested so that this task is marked failed
                    cts.Token.ThrowIfCancellationRequested();

                    Util.SaveManifestToFile(configDir, newManifest);
                }
            }

            cts.Token.ThrowIfCancellationRequested();

            if (newManifest.FilenamesEncrypted)
            {
                if (!newManifest.DecryptFilenames(depot.DepotKey))
                {
                    (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Failed to decrypt filenames in manifest {0} for depot {1}.", depot.ManifestId, depot.DepotId);
                    return null;
                }
            }

            (Config.JsonProgress ? Console.Error : Console.Out).WriteLine("Manifest {0} ({1})", depot.ManifestId, newManifest.CreationTime);

            if (Config.DownloadManifestOnly)
            {
                DumpManifestToTextFile(depot, newManifest);
                return null;
            }

            var stagingDir = Path.Combine(depot.InstallDir, STAGING_DIR);

            var filesAfterExclusions = newManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).ToList();
            var allFileNames = new HashSet<string>(filesAfterExclusions.Count);

            // Pre-process
            filesAfterExclusions.ForEach(file =>
            {
                allFileNames.Add(file.FileName);

                var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                var fileStagingPath = Path.Combine(stagingDir, file.FileName);

                if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                {
                    Directory.CreateDirectory(fileFinalPath);
                    Directory.CreateDirectory(fileStagingPath);
                }
                else
                {
                    // Some manifests don't explicitly include all necessary directories
                    Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath));
                    Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath));

                    downloadCounter.completeDownloadSize += file.TotalSize;
                    depotCounter.completeDownloadSize += file.TotalSize;

                }
            });

            return new DepotFilesData
            {
                depotDownloadInfo = depot,
                depotCounter = depotCounter,
                stagingDir = stagingDir,
                manifest = newManifest,
                previousManifest = oldManifest,
                filteredFiles = filesAfterExclusions,
                allFileNames = allFileNames
            };
        }

        internal static (ulong bytes, int chunks) GetVerifyWorkForExistingFile(
            DepotManifest.FileData file,
            DepotManifest.FileData oldManifestFile,
            ResumeStateStore resumeStateStore,
            string fileFinalPath)
        {
            var fileInfo = new FileInfo(fileFinalPath);

            if (ShouldTreatExistingFileAsPreallocatedEmpty(resumeStateStore, file, fileInfo))
            {
                return (0, 0);
            }

            if (resumeStateStore?.CanUseForResume == true
                && resumeStateStore.State.HasCompletedChunks(file)
                && fileInfo.Length == (long)file.TotalSize)
            {
                return (0, 0);
            }

            if (oldManifestFile == null)
            {
                return (file.TotalSize, file.Chunks.Count);
            }

            if (!Config.VerifyAll && oldManifestFile.FileHash.SequenceEqual(file.FileHash))
            {
                return (0, 0);
            }

            ulong bytes = 0;
            var chunks = 0;

            foreach (var chunk in file.Chunks)
            {
                var oldChunk = oldManifestFile.Chunks.FirstOrDefault(c => c.ChunkID.SequenceEqual(chunk.ChunkID));
                if (oldChunk == null)
                {
                    continue;
                }

                bytes += oldChunk.UncompressedLength;
                chunks++;
            }

            return (bytes, chunks);
        }

        // Per-depot wrapper that records success/failure into the app-level
        // tallies and emits the depot_done NDJSON event. Re-throws to preserve
        // the existing "depot failure aborts the app" semantics.
        private static async Task RunDepotDownloadAsync(
            CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter,
            DepotFilesData depotFilesData,
            HashSet<string> allFileNamesAllDepots,
            List<uint> depotsOk,
            List<uint> depotsFailed)
        {
            var depotId = depotFilesData.depotDownloadInfo.DepotId;
            try
            {
                await DownloadSteam3AsyncDepotFiles(cts, downloadCounter, depotFilesData, allFileNamesAllDepots);
                depotsOk.Add(depotId);
                JsonProgressLogger.EmitDepotDone(depotId, success: true);
            }
            catch (OperationCanceledException)
            {
                // User cancelled — propagate without recording as a failed depot.
                throw;
            }
            catch (Exception ex)
            {
                depotsFailed.Add(depotId);
                JsonProgressLogger.EmitDepotDone(depotId, success: false, error: ex.Message);
                throw;
            }
        }

        private static async Task DownloadSteam3AsyncDepotFiles(CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter, DepotFilesData depotFilesData, HashSet<string> allFileNamesAllDepots)
        {
            var depot = depotFilesData.depotDownloadInfo;
            var depotCounter = depotFilesData.depotCounter;

            downloadCounter.InteractiveLog("Downloading depot {0}", depot.DepotId);
            downloadCounter.SetCurrentDepot(depot.DepotId);

            var files = depotFilesData.filteredFiles.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).ToArray();
            var networkChunkQueue = new ConcurrentQueue<(FileStreamData fileStreamData, DepotManifest.FileData fileData, DepotManifest.ChunkData chunk)>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Config.MaxDownloads,
                CancellationToken = cts.Token
            };

            try
            {
                await Parallel.ForEachAsync(files, parallelOptions, async (file, cancellationToken) =>
                {
                    await Task.Yield();
                    DownloadSteam3AsyncDepotFile(cts, downloadCounter, depotFilesData, file, networkChunkQueue);
                });

                await Parallel.ForEachAsync(networkChunkQueue, parallelOptions, async (q, cancellationToken) =>
                {
                    await DownloadSteam3AsyncDepotFileChunk(
                        cts, downloadCounter, depotFilesData,
                        q.fileData, q.fileStreamData, q.chunk
                    );
                });
            }
            finally
            {
                depotFilesData.resumeStateStore?.SaveIfDirty(downloadCounter, force: true);
            }

            // Check for deleted files if updating the depot.
            if (depotFilesData.previousManifest != null)
            {
                var previousFilteredFiles = depotFilesData.previousManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).Select(f => f.FileName).ToHashSet();

                // Check if we are writing to a single output directory. If not, each depot folder is managed independently
                if (string.IsNullOrWhiteSpace(Config.InstallDirectory))
                {
                    // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names
                    previousFilteredFiles.ExceptWith(depotFilesData.allFileNames);
                }
                else
                {
                    // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names across all depots being downloaded
                    previousFilteredFiles.ExceptWith(allFileNamesAllDepots);
                }

                foreach (var existingFileName in previousFilteredFiles)
                {
                    var fileFinalPath = Path.Combine(depot.InstallDir, existingFileName);

                    if (!File.Exists(fileFinalPath))
                        continue;

                    File.Delete(fileFinalPath);
                    downloadCounter.InteractiveLog("Deleted {0}", fileFinalPath);
                }
            }

            DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = depot.ManifestId;
            DepotConfigStore.Save();
            depotFilesData.resumeStateStore?.Delete(downloadCounter);

            downloadCounter.InteractiveLog("Depot {0} - Downloaded {1} bytes ({2} bytes uncompressed)", depot.DepotId, depotCounter.depotBytesCompressed, depotCounter.depotBytesUncompressed);
        }

        private static void DownloadSteam3AsyncDepotFile(
            CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter,
            DepotFilesData depotFilesData,
            DepotManifest.FileData file,
            ConcurrentQueue<(FileStreamData, DepotManifest.FileData, DepotManifest.ChunkData)> networkChunkQueue)
        {
            cts.Token.ThrowIfCancellationRequested();

            var depot = depotFilesData.depotDownloadInfo;
            var stagingDir = depotFilesData.stagingDir;
            var depotDownloadCounter = depotFilesData.depotCounter;
            var oldProtoManifest = depotFilesData.previousManifest;
            DepotManifest.FileData oldManifestFile = null;
            if (oldProtoManifest != null)
            {
                oldManifestFile = oldProtoManifest.Files.SingleOrDefault(f => f.FileName == file.FileName);
            }

            var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
            var fileStagingPath = Path.Combine(stagingDir, file.FileName);

            // This may still exist if the previous run exited before cleanup
            if (File.Exists(fileStagingPath))
            {
                File.Delete(fileStagingPath);
            }

            List<DepotManifest.ChunkData> neededChunks;
            var fi = new FileInfo(fileFinalPath);
            var fileDidExist = fi.Exists;
            if (!fileDidExist)
            {
                downloadCounter.InteractiveLog("Pre-allocating {0}", fileFinalPath);

                // create new file. need all chunks
                using var fs = File.Create(fileFinalPath);
                try
                {
                    fs.SetLength((long)file.TotalSize);
                }
                catch (IOException ex)
                {
                    throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
                }

                depotFilesData.resumeStateStore?.ClearFile(file, downloadCounter);
                neededChunks = new List<DepotManifest.ChunkData>(file.Chunks);
            }
            else
            {
                var resumeStateStore = depotFilesData.resumeStateStore;
                if (ShouldTreatExistingFileAsPreallocatedEmpty(resumeStateStore, file, fi))
                {
                    neededChunks = new List<DepotManifest.ChunkData>(file.Chunks);
                }
                else if (resumeStateStore?.CanUseForResume == true
                    && !Config.VerifyAll
                    && (ulong)fi.Length == file.TotalSize
                    && resumeStateStore.State.HasCompletedChunks(file))
                {
                    neededChunks = [];

                    foreach (var chunk in file.Chunks)
                    {
                        if (resumeStateStore.State.IsChunkCompleted(file, chunk))
                        {
                            CreditCompletedChunk(downloadCounter, depotDownloadCounter, chunk);
                        }
                        else
                        {
                            neededChunks.Add(chunk);
                        }
                    }

                    if (neededChunks.Count == 0)
                    {
                        float depotPercent;
                        lock (depotDownloadCounter)
                        {
                            depotPercent = (depotDownloadCounter.sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f;
                        }

                        downloadCounter.FileCompleted(depotPercent, fileFinalPath);

                        return;
                    }
                }
                else
                {
                    if (resumeStateStore?.CanUseForResume == true && resumeStateStore.State.HasCompletedChunks(file))
                    {
                        resumeStateStore.ClearFile(file, downloadCounter);
                    }

                    // open existing
                    var didValidatePerChunk = false;

                    if (oldManifestFile != null)
                    {
                        neededChunks = [];

                        var hashMatches = oldManifestFile.FileHash.SequenceEqual(file.FileHash);
                        if (Config.VerifyAll || !hashMatches)
                        {
                            // we have a version of this file, but it doesn't fully match what we want
                            if (Config.VerifyAll)
                            {
                                downloadCounter.InteractiveLog("Validating {0}", fileFinalPath);
                            }

                            var matchingChunks = new List<ChunkMatch>();

                            foreach (var chunk in file.Chunks)
                            {
                                var oldChunk = oldManifestFile.Chunks.FirstOrDefault(c => c.ChunkID.SequenceEqual(chunk.ChunkID));
                                if (oldChunk != null)
                                {
                                    matchingChunks.Add(new ChunkMatch(oldChunk, chunk));
                                }
                                else
                                {
                                    neededChunks.Add(chunk);
                                }
                            }

                            var orderedChunks = matchingChunks.OrderBy(x => x.OldChunk.Offset);

                            var copyChunks = new List<ChunkMatch>();

                            didValidatePerChunk = true;

                            using (var fsOld = File.Open(fileFinalPath, FileMode.Open))
                            {
                                foreach (var match in orderedChunks)
                                {
                                    fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                                    var adler = Util.AdlerHash(fsOld, (int)match.OldChunk.UncompressedLength);
                                    downloadCounter.AddVerifiedChunk(match.OldChunk.UncompressedLength);

                                    if (!adler.SequenceEqual(BitConverter.GetBytes(match.OldChunk.Checksum)))
                                    {
                                        neededChunks.Add(match.NewChunk);
                                    }
                                    else
                                    {
                                        copyChunks.Add(match);

                                        lock (depotDownloadCounter)
                                        {
                                            depotDownloadCounter.sizeDownloaded += match.NewChunk.UncompressedLength;
                                        }

                                        downloadCounter.AddCompletedBytes(match.NewChunk.UncompressedLength);
                                        downloadCounter.AddCompletedChunks(1);
                                        RecordValidatedChunkCompleted(depotFilesData.resumeStateStore, file, match.NewChunk, matched: true, downloadCounter);
                                    }
                                }
                            }

                            if (!hashMatches || neededChunks.Count > 0)
                            {
                                File.Move(fileFinalPath, fileStagingPath);

                                using (var fsOld = File.Open(fileStagingPath, FileMode.Open))
                                {
                                    using var fs = File.Open(fileFinalPath, FileMode.Create);
                                    try
                                    {
                                        fs.SetLength((long)file.TotalSize);
                                    }
                                    catch (IOException ex)
                                    {
                                        throw new ContentDownloaderException(string.Format("Failed to resize file to expected size {0}: {1}", fileFinalPath, ex.Message));
                                    }

                                    foreach (var match in copyChunks)
                                    {
                                        fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                                        var tmp = new byte[match.OldChunk.UncompressedLength];
                                        fsOld.ReadExactly(tmp);

                                        fs.Seek((long)match.NewChunk.Offset, SeekOrigin.Begin);
                                        fs.Write(tmp, 0, tmp.Length);

                                        depotFilesData.resumeStateStore?.MarkChunkCompleted(file, match.NewChunk, downloadCounter);
                                    }
                                }

                                File.Delete(fileStagingPath);
                            }
                        }
                    }
                    else
                    {
                        // No old manifest or file not in old manifest. We must validate.

                        using var fs = File.Open(fileFinalPath, FileMode.Open);
                        if ((ulong)fi.Length != file.TotalSize)
                        {
                            try
                            {
                                fs.SetLength((long)file.TotalSize);
                            }
                            catch (IOException ex)
                            {
                                throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
                            }
                        }

                        downloadCounter.InteractiveLog("Validating {0}", fileFinalPath);
                        didValidatePerChunk = true;

                        neededChunks = Util.ValidateSteam3FileChecksums(
                            fs,
                            [.. file.Chunks.OrderBy(x => x.Offset)],
                            (chunk, matched) =>
                            {
                                downloadCounter.AddVerifiedChunk(chunk.UncompressedLength);

                                if (matched)
                                {
                                    lock (depotDownloadCounter)
                                    {
                                        depotDownloadCounter.sizeDownloaded += chunk.UncompressedLength;
                                    }

                                    downloadCounter.AddCompletedBytes(chunk.UncompressedLength);
                                    downloadCounter.AddCompletedChunks(1);
                                    RecordValidatedChunkCompleted(depotFilesData.resumeStateStore, file, chunk, matched: true, downloadCounter);
                                }
                            });
                    }

                    if (neededChunks.Count == 0)
                    {
                        if (!didValidatePerChunk)
                        {
                            // Old manifest hash matched and verification was skipped - credit the entire file at once.
                            lock (depotDownloadCounter)
                            {
                                depotDownloadCounter.sizeDownloaded += file.TotalSize;
                            }

                            downloadCounter.AddCompletedBytes(file.TotalSize);
                            downloadCounter.AddCompletedChunks(file.Chunks.Count);
                        }

                        float depotPercent;
                        lock (depotDownloadCounter)
                        {
                            depotPercent = (depotDownloadCounter.sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f;
                        }

                        downloadCounter.FileCompleted(depotPercent, fileFinalPath);

                        return;
                    }

                    // Per-chunk validation already credited the matched bytes to sizeDownloaded / completeDownloadSize.
                }
            }

            var fileIsExecutable = file.Flags.HasFlag(EDepotFileFlag.Executable);
            if (fileIsExecutable && (!fileDidExist || oldManifestFile == null || !oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable)))
            {
                PlatformUtilities.SetExecutable(fileFinalPath, true);
            }
            else if (!fileIsExecutable && oldManifestFile != null && oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable))
            {
                PlatformUtilities.SetExecutable(fileFinalPath, false);
            }

            var fileStreamData = new FileStreamData
            {
                fileStream = null,
                fileLock = new SemaphoreSlim(1),
                chunksToDownload = neededChunks.Count
            };

            foreach (var chunk in neededChunks)
            {
                networkChunkQueue.Enqueue((fileStreamData, file, chunk));
            }
        }

        private static async Task DownloadSteam3AsyncDepotFileChunk(
            CancellationTokenSource cts,
            GlobalDownloadCounter downloadCounter,
            DepotFilesData depotFilesData,
            DepotManifest.FileData file,
            FileStreamData fileStreamData,
            DepotManifest.ChunkData chunk)
        {
            cts.Token.ThrowIfCancellationRequested();

            var depot = depotFilesData.depotDownloadInfo;
            var depotDownloadCounter = depotFilesData.depotCounter;

            var chunkID = Convert.ToHexString(chunk.ChunkID).ToLowerInvariant();

            var written = 0;
            var chunkBuffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);

            try
            {
                do
                {
                    cts.Token.ThrowIfCancellationRequested();

                    Server connection = null;

                    try
                    {
                        connection = cdnPool.GetConnection();

                        string cdnToken = null;
                        if (steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
                        {
                            var result = await authTokenCallbackPromise.Task;
                            cdnToken = result.Token;
                        }

                        DebugLog.WriteLine("ContentDownloader", "Downloading chunk {0} from {1} with {2}", chunkID, connection, cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
                        written = await cdnPool.CDNClient.DownloadDepotChunkAsync(
                            depot.DepotId,
                            chunk,
                            connection,
                            chunkBuffer,
                            depot.DepotKey,
                            cdnPool.ProxyServer,
                            cdnToken).ConfigureAwait(false);

                        cdnPool.ReturnConnection(connection);

                        break;
                    }
                    catch (TaskCanceledException)
                    {
                        downloadCounter.Log("Connection timeout downloading chunk {0}", chunkID);
                        cdnPool.ReturnBrokenConnection(connection);
                    }
                    catch (SteamKitWebRequestException e)
                    {
                        // If the CDN returned 403, attempt to get a cdn auth if we didn't yet,
                        // if auth task already exists, make sure it didn't complete yet, so that it gets awaited above
                        if (e.StatusCode == HttpStatusCode.Forbidden &&
                            (!steam3.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise) || !authTokenCallbackPromise.Task.IsCompleted))
                        {
                            await steam3.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);

                            cdnPool.ReturnConnection(connection);

                            continue;
                        }

                        cdnPool.ReturnBrokenConnection(connection);

                        if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                        {
                            downloadCounter.Log("Encountered {1} for chunk {0}. Aborting.", chunkID, (int)e.StatusCode);
                            break;
                        }

                        downloadCounter.Log("Encountered error downloading chunk {0}: {1}", chunkID, e.StatusCode);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        cdnPool.ReturnBrokenConnection(connection);
                        downloadCounter.Log("Encountered unexpected error downloading chunk {0}: {1}", chunkID, e.Message);
                    }
                } while (written == 0);

                if (written == 0)
                {
                    downloadCounter.Log("Failed to find any server with chunk {0} for depot {1}. Aborting.", chunkID, depot.DepotId);
                    cts.Cancel();
                }

                // Throw the cancellation exception if requested so that this task is marked failed
                cts.Token.ThrowIfCancellationRequested();

                try
                {
                    await fileStreamData.fileLock.WaitAsync().ConfigureAwait(false);

                    if (fileStreamData.fileStream == null)
                    {
                        var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                        fileStreamData.fileStream = File.Open(fileFinalPath, FileMode.Open);
                    }

                    fileStreamData.fileStream.Seek((long)chunk.Offset, SeekOrigin.Begin);
                    await fileStreamData.fileStream.WriteAsync(chunkBuffer.AsMemory(0, written), cts.Token);
                }
                finally
                {
                    fileStreamData.fileLock.Release();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkBuffer);
            }

            var remainingChunks = Interlocked.Decrement(ref fileStreamData.chunksToDownload);
            if (remainingChunks == 0)
            {
                fileStreamData.fileStream?.Dispose();
                fileStreamData.fileLock.Dispose();
            }

            ulong sizeDownloaded;
            ulong depotTotalSize;
            lock (depotDownloadCounter)
            {
                sizeDownloaded = depotDownloadCounter.sizeDownloaded + (ulong)written;
                depotDownloadCounter.sizeDownloaded = sizeDownloaded;
                depotDownloadCounter.depotBytesCompressed += chunk.CompressedLength;
                depotDownloadCounter.depotBytesUncompressed += chunk.UncompressedLength;
                depotDownloadCounter.completedChunks++;
                depotTotalSize = depotDownloadCounter.completeDownloadSize;
            }

            depotFilesData.resumeStateStore?.MarkChunkCompleted(file, chunk, downloadCounter);
            downloadCounter.AddCompletedChunk(chunk.UncompressedLength, chunk.CompressedLength, (ulong)written);

            // Emit a per-depot progress event. The logger throttles to <= 4/sec
            // per depot, so calling on every chunk credit is safe.
            var percent = depotTotalSize > 0 ? (sizeDownloaded * 100.0 / depotTotalSize) : 0.0;
            JsonProgressLogger.EmitProgress(depot.DepotId, sizeDownloaded, percent, speedBps: 0, etaSec: 0);

            if (remainingChunks == 0)
            {
                var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
                downloadCounter.FileCompleted((sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f, fileFinalPath);
            }
        }

        class ChunkIdComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] x, byte[] y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                return x.SequenceEqual(y);
            }

            public int GetHashCode(byte[] obj)
            {
                ArgumentNullException.ThrowIfNull(obj);

                // ChunkID is SHA-1, so we can just use the first 4 bytes
                return BitConverter.ToInt32(obj, 0);
            }
        }

        static void DumpManifestToTextFile(DepotDownloadInfo depot, DepotManifest manifest)
        {
            var txtManifest = Path.Combine(depot.InstallDir, $"manifest_{depot.DepotId}_{depot.ManifestId}.txt");
            using var sw = new StreamWriter(txtManifest);

            sw.WriteLine($"Content Manifest for Depot {depot.DepotId} ");
            sw.WriteLine();
            sw.WriteLine($"Manifest ID / date     : {depot.ManifestId} / {manifest.CreationTime} ");

            var uniqueChunks = new HashSet<byte[]>(new ChunkIdComparer());

            foreach (var file in manifest.Files)
            {
                foreach (var chunk in file.Chunks)
                {
                    uniqueChunks.Add(chunk.ChunkID);
                }
            }

            sw.WriteLine($"Total number of files  : {manifest.Files.Count} ");
            sw.WriteLine($"Total number of chunks : {uniqueChunks.Count} ");
            sw.WriteLine($"Total bytes on disk    : {manifest.TotalUncompressedSize} ");
            sw.WriteLine($"Total bytes compressed : {manifest.TotalCompressedSize} ");
            sw.WriteLine();
            sw.WriteLine();
            sw.WriteLine("          Size Chunks File SHA                                 Flags Name");

            foreach (var file in manifest.Files)
            {
                var sha1Hash = Convert.ToHexString(file.FileHash).ToLower();
                sw.WriteLine($"{file.TotalSize,14:d} {file.Chunks.Count,6:d} {sha1Hash} {(int)file.Flags,5:x} {file.FileName}");
            }
        }
    }
}
