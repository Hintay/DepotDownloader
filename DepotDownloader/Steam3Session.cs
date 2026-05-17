// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace DepotDownloader
{
    class Steam3Session
    {
        public bool IsLoggedOn { get; private set; }

        public ReadOnlyCollection<SteamApps.LicenseListCallback.License> Licenses
        {
            get;
            private set;
        }

        public Dictionary<uint, ulong> AppTokens { get; } = [];
        public Dictionary<uint, ulong> PackageTokens { get; } = [];
        public Dictionary<uint, byte[]> DepotKeys { get; } = [];
        public ConcurrentDictionary<(uint, string), TaskCompletionSource<SteamContent.CDNAuthToken>> CDNAuthTokens { get; } = [];
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfo { get; } = [];
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; } = [];
        public Dictionary<string, byte[]> AppBetaPasswords { get; } = [];

        public SteamClient steamClient;
        public SteamUser steamUser;
        public SteamContent steamContent;
        readonly SteamApps steamApps;
        readonly SteamCloud steamCloud;
        readonly PublishedFile steamPublishedFile;

        readonly CallbackManager callbacks;

        readonly bool authenticatedUser;
        bool bConnecting;
        bool bAborted;
        bool bExpectingDisconnectRemote;
        bool bDidDisconnect;
        bool bIsConnectionRecovery;
        int connectionBackoff;
        int seq; // more hack fixes
        AuthSession authSession;
        readonly CancellationTokenSource abortedToken = new();

        // input
        readonly SteamUser.LogOnDetails logonDetails;

        public Steam3Session(SteamUser.LogOnDetails details)
        {
            this.logonDetails = details;
            this.authenticatedUser = details.Username != null || ContentDownloader.Config.UseQrCode;

            var clientConfiguration = SteamConfiguration.Create(config =>
                config
                    .WithHttpClientFactory(static purpose => HttpClientFactory.CreateHttpClient())
            );

            this.steamClient = new SteamClient(clientConfiguration);

            this.steamUser = this.steamClient.GetHandler<SteamUser>();
            this.steamApps = this.steamClient.GetHandler<SteamApps>();
            this.steamCloud = this.steamClient.GetHandler<SteamCloud>();
            var steamUnifiedMessages = this.steamClient.GetHandler<SteamUnifiedMessages>();
            this.steamPublishedFile = steamUnifiedMessages.CreateService<PublishedFile>();
            this.steamContent = this.steamClient.GetHandler<SteamContent>();

            this.callbacks = new CallbackManager(this.steamClient);

            this.callbacks.Subscribe<SteamClient.ConnectedCallback>(ConnectedCallback);
            this.callbacks.Subscribe<SteamClient.DisconnectedCallback>(DisconnectedCallback);
            this.callbacks.Subscribe<SteamUser.LoggedOnCallback>(LogOnCallback);
            this.callbacks.Subscribe<SteamApps.LicenseListCallback>(LicenseListCallback);

            Ansi.LogWrite("Connecting to Steam3...");
            Connect();
        }

        public delegate bool WaitCondition();

        private readonly Lock steamLock = new();

        public bool WaitUntilCallback(Action submitter, WaitCondition waiter)
        {
            while (!bAborted && !waiter())
            {
                lock (steamLock)
                {
                    submitter();
                }

                var seq = this.seq;
                do
                {
                    lock (steamLock)
                    {
                        callbacks.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                    }
                } while (!bAborted && this.seq == seq && !waiter());
            }

            return bAborted;
        }

        public bool WaitForCredentials()
        {
            if (IsLoggedOn || bAborted)
                return IsLoggedOn;

            WaitUntilCallback(() => { }, () => IsLoggedOn);

            return IsLoggedOn;
        }

        public async Task TickCallbacks()
        {
            var token = abortedToken.Token;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await callbacks.RunWaitCallbackAsync(token);
                }
            }
            catch (OperationCanceledException)
            {
                //
            }
        }

        public async Task RequestAppInfo(uint appId, bool bForce = false)
        {
            if ((AppInfo.ContainsKey(appId) && !bForce) || bAborted)
                return;

            ulong luaToken = 0;
            var hasLuaToken = ContentDownloader.Config.LuaAppTokens != null
                && ContentDownloader.Config.LuaAppTokens.TryGetValue(appId, out luaToken);

            var appTokens = await steamApps.PICSGetAccessTokens([appId], []);

            if (appTokens.AppTokensDenied.Contains(appId) && !hasLuaToken)
            {
                Console.WriteLine("Insufficient privileges to get access token for app {0}", appId);
            }

            foreach (var token_dict in appTokens.AppTokens)
            {
                this.AppTokens[token_dict.Key] = token_dict.Value;
            }

            // Lua-provided tokens win over PICS so restricted DLCs listed in the
            // Lua file can still resolve when Steam denies the access request.
            if (hasLuaToken)
            {
                this.AppTokens[appId] = luaToken;
            }

            var request = new SteamApps.PICSRequest(appId);

            if (AppTokens.TryGetValue(appId, out var token))
            {
                request.AccessToken = token;
            }

            if (TokenCFG.useAppToken)
            {
                request.AccessToken = TokenCFG.appToken;
            }

            var appInfoMultiple = await steamApps.PICSGetProductInfo([request], []);

            foreach (var appInfo in appInfoMultiple.Results)
            {
                foreach (var app_value in appInfo.Apps)
                {
                    var app = app_value.Value;

                    Console.WriteLine("Got AppInfo for {0}", app.ID);
                    AppInfo[app.ID] = app;
                }

                foreach (var app in appInfo.UnknownApps)
                {
                    AppInfo[app] = null;
                }
            }
        }

        public async Task RequestAppInfoMany(IEnumerable<uint> appIds, bool bForce = false)
        {
            if (bAborted) return;

            var toFetch = appIds.Where(id => bForce || !AppInfo.ContainsKey(id)).Distinct().ToList();
            if (toFetch.Count == 0) return;

            var tokens = await steamApps.PICSGetAccessTokens(toFetch, []);

            foreach (var deniedId in tokens.AppTokensDenied)
            {
                var hasLuaOverride = ContentDownloader.Config.LuaAppTokens != null
                    && ContentDownloader.Config.LuaAppTokens.ContainsKey(deniedId);
                if (!hasLuaOverride)
                {
                    Ansi.LogLine("Insufficient privileges to get access token for app {0}", deniedId);
                }
            }

            foreach (var kvp in tokens.AppTokens)
            {
                this.AppTokens[kvp.Key] = kvp.Value;
            }

            // Apply Lua-provided tokens as overrides (matches RequestAppInfo's single-app behavior).
            if (ContentDownloader.Config.LuaAppTokens != null)
            {
                foreach (var id in toFetch)
                {
                    if (ContentDownloader.Config.LuaAppTokens.TryGetValue(id, out var luaToken))
                    {
                        this.AppTokens[id] = luaToken;
                    }
                }
            }

            var requests = toFetch.Select(id =>
            {
                var req = new SteamApps.PICSRequest(id);
                if (AppTokens.TryGetValue(id, out var token))
                {
                    req.AccessToken = token;
                }
                if (TokenCFG.useAppToken)
                {
                    req.AccessToken = TokenCFG.appToken;
                }
                return req;
            }).ToList();

            var appInfoMultiple = await steamApps.PICSGetProductInfo(requests, []);

            foreach (var appInfo in appInfoMultiple.Results)
            {
                foreach (var app_value in appInfo.Apps)
                {
                    var app = app_value.Value;
                    Ansi.LogLine("Got AppInfo for {0}", app.ID);
                    AppInfo[app.ID] = app;
                }

                foreach (var app in appInfo.UnknownApps)
                {
                    AppInfo[app] = null;
                }
            }
        }

        public async Task RequestPackageInfo(IEnumerable<uint> packageIds)
        {
            var packages = packageIds.ToList();
            packages.RemoveAll(PackageInfo.ContainsKey);

            if (packages.Count == 0 || bAborted)
                return;

            var packageRequests = new List<SteamApps.PICSRequest>();

            foreach (var package in packages)
            {
                var request = new SteamApps.PICSRequest(package);

                if (PackageTokens.TryGetValue(package, out var token))
                {
                    request.AccessToken = token;
                }

                if (TokenCFG.usePackageToken)
                {
                    request.AccessToken = TokenCFG.packageToken;
                }

                packageRequests.Add(request);
            }

            var packageInfoMultiple = await steamApps.PICSGetProductInfo([], packageRequests);

            foreach (var packageInfo in packageInfoMultiple.Results)
            {
                foreach (var package_value in packageInfo.Packages)
                {
                    var package = package_value.Value;
                    PackageInfo[package.ID] = package;
                }

                foreach (var package in packageInfo.UnknownPackages)
                {
                    PackageInfo[package] = null;
                }
            }
        }

        public async Task<bool> RequestFreeAppLicense(uint appId)
        {
            try
            {
                var resultInfo = await steamApps.RequestFreeLicense(appId);

                return resultInfo.GrantedApps.Contains(appId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to request FreeOnDemand license for app {appId}: {ex.Message}");
                return false;
            }
        }

        public async Task RequestDepotKey(uint depotId, uint appid = 0)
        {
            if (DepotKeys.ContainsKey(depotId) || bAborted)
                return;

            var depotKey = await steamApps.GetDepotDecryptionKey(depotId, appid);

            Ansi.LogLine("Got depot key for {0} result: {1}", depotKey.DepotID, depotKey.Result);

            if (depotKey.Result != EResult.OK)
            {
                return;
            }

            DepotKeys[depotKey.DepotID] = depotKey.DepotKey;
        }


        public async Task<ulong> GetDepotManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
        {
            if (bAborted)
                return 0;

            var requestCode = await steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch);

            if (requestCode == 0)
            {
                Ansi.LogLine($"No manifest request code was returned for depot {depotId} from app {appId}, manifest {manifestId}");

                if (!authenticatedUser)
                {
                    Ansi.LogLine("Suggestion: Try logging in with -username as old manifests may not be available for anonymous accounts.");
                }
            }
            else
            {
                Ansi.LogLine($"Got manifest request code for depot {depotId} from app {appId}, manifest {manifestId}, result: {requestCode}");
            }

            return requestCode;
        }

        public async Task RequestCDNAuthToken(uint appid, uint depotid, Server server)
        {
            var cdnKey = (depotid, server.Host);
            var completion = new TaskCompletionSource<SteamContent.CDNAuthToken>();

            if (bAborted || !CDNAuthTokens.TryAdd(cdnKey, completion))
            {
                return;
            }

            DebugLog.WriteLine(nameof(Steam3Session), $"Requesting CDN auth token for {server.Host}");

            var cdnAuth = await steamContent.GetCDNAuthToken(appid, depotid, server.Host);

            Ansi.LogLine($"Got CDN auth token for {server.Host} result: {cdnAuth.Result} (expires {cdnAuth.Expiration})");

            if (cdnAuth.Result != EResult.OK)
            {
                return;
            }

            completion.TrySetResult(cdnAuth);
        }

        public async Task CheckAppBetaPassword(uint appid, string password)
        {
            var appPassword = await steamApps.CheckAppBetaPassword(appid, password);

            Ansi.LogLine("Retrieved {0} beta keys with result: {1}", appPassword.BetaPasswords.Count, appPassword.Result);

            foreach (var entry in appPassword.BetaPasswords)
            {
                AppBetaPasswords[entry.Key] = entry.Value;
            }
        }

        public async Task<KeyValue> GetPrivateBetaDepotSection(uint appid, string branch)
        {
            if (!AppBetaPasswords.TryGetValue(branch, out var branchPassword)) // Should be filled by CheckAppBetaPassword
            {
                return new KeyValue();
            }

            AppTokens.TryGetValue(appid, out var accessToken); // Should be filled by RequestAppInfo

            if (TokenCFG.useAppToken)
            {
                accessToken = TokenCFG.appToken;
            }

            var privateBeta = await steamApps.PICSGetPrivateBeta(appid, accessToken, branch, branchPassword);

            Ansi.LogLine($"Retrieved private beta depot section for {appid} with result: {privateBeta.Result}");

            return privateBeta.DepotSection;
        }

        public async Task<PublishedFileDetails> GetPublishedFileDetails(uint appId, PublishedFileID pubFile)
        {
            var pubFileRequest = new CPublishedFile_GetDetails_Request
            {
                appid = appId,
                includechildren = true,
            };
            pubFileRequest.publishedfileids.Add(pubFile);

            var details = await steamPublishedFile.GetDetails(pubFileRequest);

            if (details.Result == EResult.OK)
            {
                return details.Body.publishedfiledetails.FirstOrDefault();
            }

            throw new Exception($"EResult {(int)details.Result} ({details.Result}) while retrieving file details for pubfile {pubFile}.");
        }


        public async Task<SteamCloud.UGCDetailsCallback> GetUGCDetails(UGCHandle ugcHandle)
        {
            var callback = await steamCloud.RequestUGCDetails(ugcHandle);

            if (callback.Result == EResult.OK)
            {
                return callback;
            }
            else if (callback.Result == EResult.FileNotFound)
            {
                return null;
            }

            throw new Exception($"EResult {(int)callback.Result} ({callback.Result}) while retrieving UGC details for {ugcHandle}.");
        }

        private void ResetConnectionFlags()
        {
            bExpectingDisconnectRemote = false;
            bDidDisconnect = false;
            bIsConnectionRecovery = false;
        }

        void Connect()
        {
            bAborted = false;
            bConnecting = true;
            connectionBackoff = 0;
            authSession = null;

            ResetConnectionFlags();
            this.steamClient.Connect();
        }

        private void Abort(bool sendLogOff = true)
        {
            Disconnect(sendLogOff);
        }

        public void Disconnect(bool sendLogOff = true)
        {
            if (sendLogOff)
            {
                steamUser.LogOff();
            }

            bAborted = true;
            bConnecting = false;
            bIsConnectionRecovery = false;
            abortedToken.Cancel();
            steamClient.Disconnect();

            Ansi.Progress(Ansi.ProgressState.Hidden);

            // flush callbacks until our disconnected event
            while (!bDidDisconnect)
            {
                callbacks.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
            }
        }

        private void Reconnect()
        {
            bIsConnectionRecovery = true;
            steamClient.Disconnect();
        }

        private async void ConnectedCallback(SteamClient.ConnectedCallback connected)
        {
            Ansi.LogLine(" Done!");
            bConnecting = false;

            // Update our tracking so that we don't time out, even if we need to reconnect multiple times,
            // e.g. if the authentication phase takes a while and therefore multiple connections.
            connectionBackoff = 0;

            if (!authenticatedUser)
            {
                Ansi.LogWrite("Logging anonymously into Steam3...");
                steamUser.LogOnAnonymous();
            }
            else
            {
                if (logonDetails.Username != null)
                {
                    Ansi.LogLine("Logging '{0}' into Steam3...", logonDetails.Username);
                }

                if (authSession is null)
                {
                    if (logonDetails.Username != null && logonDetails.Password != null && logonDetails.AccessToken is null)
                    {
                        try
                        {
                            _ = AccountSettingsStore.Instance.GuardData.TryGetValue(logonDetails.Username, out var guarddata);
                            authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                            {
                                DeviceFriendlyName = nameof(DepotDownloader),
                                Username = logonDetails.Username,
                                Password = logonDetails.Password,
                                IsPersistentSession = ContentDownloader.Config.RememberPassword,
                                GuardData = guarddata,
                                Authenticator = new ConsoleAuthenticator(),
                            });
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("Failed to authenticate with Steam: " + ex.Message);
                            Abort(false);
                            return;
                        }
                    }
                    else if (logonDetails.AccessToken is null && ContentDownloader.Config.UseQrCode)
                    {
                        Ansi.LogLine("Logging in with QR code...");

                        try
                        {
                            var session = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                            {
                                DeviceFriendlyName = nameof(DepotDownloader),
                                IsPersistentSession = ContentDownloader.Config.RememberPassword,
                            });

                            authSession = session;

                            // Steam will periodically refresh the challenge url, so we need a new QR code.
                            session.ChallengeURLChanged = () =>
                            {
                                Ansi.LogLine(string.Empty);
                                Ansi.LogLine("The QR code has changed:");

                                DisplayQrCode(session.ChallengeURL);
                            };

                            // Draw initial QR code immediately
                            DisplayQrCode(session.ChallengeURL);
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("Failed to authenticate with Steam: " + ex.Message);
                            Abort(false);
                            return;
                        }
                    }
                }

                if (authSession != null)
                {
                    try
                    {
                        var result = await authSession.PollingWaitForResultAsync();

                        logonDetails.Username = result.AccountName;
                        logonDetails.Password = null;
                        logonDetails.AccessToken = result.RefreshToken;

                        if (result.NewGuardData != null)
                        {
                            AccountSettingsStore.Instance.GuardData[result.AccountName] = result.NewGuardData;

                            if (ContentDownloader.Config.UseQrCode)
                            {
                                Ansi.LogLine($"Success! Next time you can login with -username {result.AccountName} -remember-password instead of -qr.");
                            }
                        }
                        else
                        {
                            AccountSettingsStore.Instance.GuardData.Remove(result.AccountName);
                        }

                        AccountSettingsStore.Instance.LoginTokens[result.AccountName] = result.RefreshToken;
                        AccountSettingsStore.Save();
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("Failed to authenticate with Steam: " + ex.Message);
                        Abort(false);
                        return;
                    }

                    authSession = null;
                }

                steamUser.LogOn(logonDetails);
            }
        }

        private void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected)
        {
            bDidDisconnect = true;

            DebugLog.WriteLine(nameof(Steam3Session), $"Disconnected: bIsConnectionRecovery = {bIsConnectionRecovery}, UserInitiated = {disconnected.UserInitiated}, bExpectingDisconnectRemote = {bExpectingDisconnectRemote}");

            // When recovering the connection, we want to reconnect even if the remote disconnects us
            if (!bIsConnectionRecovery && (disconnected.UserInitiated || bExpectingDisconnectRemote))
            {
                Ansi.LogLine("Disconnected from Steam");

                // Any operations outstanding need to be aborted
                bAborted = true;
            }
            else if (connectionBackoff >= 10)
            {
                Ansi.LogLine("Could not connect to Steam after 10 tries");
                Abort(false);
            }
            else if (!bAborted)
            {
                connectionBackoff += 1;

                if (bConnecting)
                {
                    Ansi.LogLine($"Connection to Steam failed. Trying again (#{connectionBackoff})...");
                }
                else
                {
                    Ansi.LogLine("Lost connection to Steam. Reconnecting");
                }

                Thread.Sleep(1000 * connectionBackoff);

                // Any connection related flags need to be reset here to match the state after Connect
                ResetConnectionFlags();
                steamClient.Connect();
            }
        }

        private void LogOnCallback(SteamUser.LoggedOnCallback loggedOn)
        {
            var isSteamGuard = loggedOn.Result == EResult.AccountLogonDenied;
            var is2FA = loggedOn.Result == EResult.AccountLoginDeniedNeedTwoFactor;
            var isAccessToken = ContentDownloader.Config.RememberPassword && logonDetails.AccessToken != null &&
                loggedOn.Result is EResult.InvalidPassword
                or EResult.InvalidSignature
                or EResult.AccessDenied
                or EResult.Expired
                or EResult.Revoked;

            if (isSteamGuard || is2FA || isAccessToken)
            {
                bExpectingDisconnectRemote = true;
                Abort(false);

                if (!isAccessToken)
                {
                    Ansi.LogLine("This account is protected by Steam Guard.");
                }

                if (is2FA)
                {
                    do
                    {
                        Console.Write("Please enter your 2 factor auth code from your authenticator app: ");
                        logonDetails.TwoFactorCode = Console.ReadLine();
                    } while (string.Empty == logonDetails.TwoFactorCode);
                }
                else if (isAccessToken)
                {
                    AccountSettingsStore.Instance.LoginTokens.Remove(logonDetails.Username);
                    AccountSettingsStore.Save();

                    // TODO: Handle gracefully by falling back to password prompt?
                    Ansi.LogLine($"Access token was rejected ({loggedOn.Result}).");
                    Abort(false);
                    return;
                }
                else
                {
                    do
                    {
                        Console.Write("Please enter the authentication code sent to your email address: ");
                        logonDetails.AuthCode = Console.ReadLine();
                    } while (string.Empty == logonDetails.AuthCode);
                }

                Ansi.LogWrite("Retrying Steam3 connection...");
                Connect();

                return;
            }

            if (loggedOn.Result == EResult.TryAnotherCM)
            {
                Ansi.LogWrite("Retrying Steam3 connection (TryAnotherCM)...");

                Reconnect();

                return;
            }

            if (loggedOn.Result == EResult.ServiceUnavailable)
            {
                Ansi.LogLine("Unable to login to Steam3: {0}", loggedOn.Result);
                Abort(false);

                return;
            }

            if (loggedOn.Result != EResult.OK)
            {
                Ansi.LogLine("Unable to login to Steam3: {0}", loggedOn.Result);
                Abort();

                return;
            }

            Ansi.LogLine(" Done!");

            this.seq++;
            IsLoggedOn = true;

            if (ContentDownloader.Config.CellID == 0)
            {
                Ansi.LogLine("Using Steam3 suggested CellID: " + loggedOn.CellID);
                ContentDownloader.Config.CellID = (int)loggedOn.CellID;
            }
        }

        private void LicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            if (licenseList.Result != EResult.OK)
            {
                Ansi.LogLine("Unable to get license list: {0} ", licenseList.Result);
                Abort();

                return;
            }

            Ansi.LogLine("Got {0} licenses for account!", licenseList.LicenseList.Count);
            Licenses = licenseList.LicenseList;

            foreach (var license in licenseList.LicenseList)
            {
                if (license.AccessToken > 0)
                {
                    PackageTokens.TryAdd(license.PackageID, license.AccessToken);
                }
            }
        }

        private static void DisplayQrCode(string challengeUrl)
        {
            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(challengeUrl, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrCodeAsAsciiArt = qrCode.GetLineByLineGraphic(1, drawQuietZones: true);

            Ansi.LogLine("Use the Steam Mobile App to sign in with this QR code:");

            foreach (var line in qrCodeAsAsciiArt)
            {
                Ansi.LogLine(line);
            }
        }
    }
}
