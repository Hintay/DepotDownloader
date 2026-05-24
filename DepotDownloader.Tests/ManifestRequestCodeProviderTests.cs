// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using DepotDownloader;
using System.Threading.Tasks;
using Xunit;

namespace DepotDownloader.Tests
{
    public class ManifestRequestCodeProviderTests
    {
        [Fact]
        public void TryParsePlainTextRequestCode_ValidUnsignedInteger_ReturnsTrue()
        {
            var success = ManifestRequestCodeProvider.TryParsePlainTextRequestCode(" 1234567890123456789\n", out var requestCode);

            Assert.True(success);
            Assert.Equal(1234567890123456789UL, requestCode);
        }

        [Theory]
        [InlineData("")]
        [InlineData("0")]
        [InlineData("not-a-number")]
        public void TryParsePlainTextRequestCode_InvalidValue_ReturnsFalse(string response)
        {
            var success = ManifestRequestCodeProvider.TryParsePlainTextRequestCode(response, out var requestCode);

            Assert.False(success);
            Assert.Equal(0UL, requestCode);
        }

        [Fact]
        public async Task GetWithFallbackAsync_SteamRequestCodePresent_DoesNotCallGmrc()
        {
            var gmrcCalled = false;

            var requestCode = await ManifestRequestCodeProvider.GetWithFallbackAsync(
                123UL,
                456UL,
                _ =>
                {
                    gmrcCalled = true;
                    return Task.FromResult(789UL);
                });

            Assert.Equal(123UL, requestCode);
            Assert.False(gmrcCalled);
        }

        [Fact]
        public async Task GetWithFallbackAsync_SteamRequestCodeMissing_UsesGmrcForManifest()
        {
            ulong requestedManifestId = 0;

            var requestCode = await ManifestRequestCodeProvider.GetWithFallbackAsync(
                0,
                456UL,
                manifestId =>
                {
                    requestedManifestId = manifestId;
                    return Task.FromResult(789UL);
                });

            Assert.Equal(789UL, requestCode);
            Assert.Equal(456UL, requestedManifestId);
        }
    }
}
