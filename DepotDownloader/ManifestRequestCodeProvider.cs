// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Threading.Tasks;
using SteamKit2;

namespace DepotDownloader
{
    static class ManifestRequestCodeProvider
    {
        public static async Task<ulong> GetFromGmrcAsync(ulong manifestId)
        {
            try
            {
                using var client = HttpClientFactory.CreateHttpClient();
                var response = await client.GetStringAsync($"https://gmrc.wudrm.com/manifest/{manifestId}").ConfigureAwait(false);

                return TryParsePlainTextRequestCode(response, out var requestCode) ? requestCode : 0;
            }
            catch (Exception ex)
            {
                DebugLog.WriteLine(nameof(ManifestRequestCodeProvider), "Unable to get GMRC manifest request code for {0}: {1}", manifestId, ex.Message);
                return 0;
            }
        }

        internal static bool TryParsePlainTextRequestCode(string response, out ulong requestCode)
        {
            requestCode = 0;
            return ulong.TryParse(response?.Trim(), out requestCode) && requestCode != 0;
        }

        internal static async Task<ulong> GetWithFallbackAsync(
            ulong steamRequestCode,
            ulong manifestId,
            Func<ulong, Task<ulong>> fallbackProvider)
        {
            if (steamRequestCode != 0)
            {
                return steamRequestCode;
            }

            return await fallbackProvider(manifestId).ConfigureAwait(false);
        }
    }
}
