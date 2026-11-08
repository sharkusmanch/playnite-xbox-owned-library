using Newtonsoft.Json;
using XboxOwnedLibrary.Services;
using Xunit;

namespace XboxOwnedLibrary.Tests
{
    // Shapes trimmed from real displaycatalog.mp.microsoft.com/v7.0/products answers.
    public class CatalogProductTests
    {
        private const string PlayAnywhereGame = @"{
          ""ProductId"": ""9PP5G1F0C2B6"",
          ""ProductKind"": ""Game"",
          ""LocalizedProperties"": [{
            ""ProductTitle"": ""Halo Infinite"",
            ""Images"": [
              { ""ImagePurpose"": ""Poster"", ""Uri"": ""//store-images.s-microsoft.com/poster-small"", ""Width"": 720, ""Height"": 1080 },
              { ""ImagePurpose"": ""Poster"", ""Uri"": ""//store-images.s-microsoft.com/poster-big"", ""Width"": 1440, ""Height"": 2160 },
              { ""ImagePurpose"": ""Logo"", ""Uri"": ""https://store-images.s-microsoft.com/logo"", ""Width"": 300, ""Height"": 300 }
            ]
          }],
          ""Properties"": {
            ""PackageFamilyName"": ""Microsoft.254428597CFE2_8wekyb3d8bbwe"",
            ""XboxConsoleGenCompatible"": [""ConsoleGen8"", ""ConsoleGen9""]
          },
          ""DisplaySkuAvailabilities"": [{ ""Sku"": { ""Properties"": {
            ""XboxXPA"": true,
            ""Packages"": [
              { ""PackageFamilyName"": """", ""PlatformDependencies"": [{ ""PlatformName"": ""Windows.Xbox"" }] },
              { ""PackageFamilyName"": ""Microsoft.254428597CFE2_8wekyb3d8bbwe"", ""PlatformDependencies"": [{ ""PlatformName"": ""Windows.Desktop"" }] }
            ]
          } } }]
        }";

        private const string ConsoleOnlyGame = @"{
          ""ProductId"": ""BZ6W9LRPC26W"",
          ""ProductKind"": ""Game"",
          ""LocalizedProperties"": [{ ""ProductTitle"": ""Control"" }],
          ""DisplaySkuAvailabilities"": [{ ""Sku"": { ""Properties"": {
            ""Packages"": [{ ""PackageFamilyName"": ""505GAMESS.P.A.ControlPCGP_tefn33qh9azfc"", ""PlatformDependencies"": [{ ""PlatformName"": ""Windows.Xbox"" }] }]
          } } }]
        }";

        private const string Bundle = @"{
          ""ProductId"": ""9NKX70BBCDRN"",
          ""ProductKind"": ""Game"",
          ""LocalizedProperties"": [{ ""ProductTitle"": ""Forza Horizon 5 Standard Edition"" }],
          ""DisplaySkuAvailabilities"": [
            { ""Sku"": { ""Properties"": { ""BundledSkus"": [{ ""BigId"": ""9nnx1vvr3knq"" }] } } },
            { ""Sku"": { ""Properties"": { ""BundledSkus"": [{ ""BigId"": ""9NNX1VVR3KNQ"" }] } } }
          ]
        }";

        // Play Anywhere, but the catalog lists only Xbox packages (no package names).
        private const string PlayAnywhereXboxPackagesOnly = @"{
          ""ProductId"": ""9N647K4PK0KV"",
          ""ProductKind"": ""Game"",
          ""LocalizedProperties"": [{ ""ProductTitle"": ""HITMAN 3"" }],
          ""Properties"": { ""PackageFamilyName"": ""IOInteractiveAS.HITMAN3-BaseGame_6h0y724g59e1w"" },
          ""DisplaySkuAvailabilities"": [{ ""Sku"": { ""Properties"": {
            ""XboxXPA"": true,
            ""Packages"": [{ ""PackageFamilyName"": """", ""PlatformDependencies"": [{ ""PlatformName"": ""Windows.Xbox"" }] }]
          } } }]
        }";

        // Not Play Anywhere, console packages only, but a product-level package name.
        private const string ConsoleOnlyWithProductPfn = @"{
          ""ProductId"": ""BZ6W9LRPC26W"",
          ""ProductKind"": ""Game"",
          ""LocalizedProperties"": [{ ""ProductTitle"": ""Control"" }],
          ""Properties"": { ""PackageFamilyName"": ""505GAMESS.P.A.ControlPCGP_tefn33qh9azfc"" },
          ""DisplaySkuAvailabilities"": [{ ""Sku"": { ""Properties"": {
            ""XboxXPA"": false,
            ""Packages"": [{ ""PackageFamilyName"": """", ""PlatformDependencies"": [{ ""PlatformName"": ""Windows.Xbox"" }] }]
          } } }]
        }";

        private static CatalogProduct Parse(string json) => JsonConvert.DeserializeObject<CatalogProduct>(json);

        [Fact]
        public void ReadsPlayAnywhereFlag()
        {
            Assert.True(Parse(PlayAnywhereGame).IsPlayAnywhere);
            Assert.False(Parse(ConsoleOnlyGame).IsPlayAnywhere);
            Assert.False(Parse(Bundle).IsPlayAnywhere);
        }

        [Fact]
        public void PlayAnywhereWithOnlyXboxPackagesUsesProductPackageName()
        {
            Assert.Equal("IOInteractiveAS.HITMAN3-BaseGame_6h0y724g59e1w", Parse(PlayAnywhereXboxPackagesOnly).PcPackageFamilyName);
        }

        [Fact]
        public void ProductPackageNameAloneDoesNotMakeAConsoleGamePc()
        {
            Assert.Null(Parse(ConsoleOnlyWithProductPfn).PcPackageFamilyName);
        }

        [Fact]
        public void FindsPcPackageNextToConsolePackages()
        {
            var product = Parse(PlayAnywhereGame);
            Assert.Equal("Microsoft.254428597CFE2_8wekyb3d8bbwe", product.PcPackageFamilyName);
            Assert.True(product.HasPackages);
        }

        [Fact]
        public void ConsoleOnlyPackageIsNotPc()
        {
            var product = Parse(ConsoleOnlyGame);
            Assert.Null(product.PcPackageFamilyName);
            Assert.True(product.HasPackages);
        }

        [Fact]
        public void BundleHasNoPackagesAndListsPartsOnce()
        {
            var product = Parse(Bundle);
            Assert.Null(product.PcPackageFamilyName);
            Assert.False(product.HasPackages);
            Assert.Equal(new[] { "9NNX1VVR3KNQ" }, product.BundledIds);
        }

        [Fact]
        public void PicksLargestImageAndAddsScheme()
        {
            var product = Parse(PlayAnywhereGame);
            Assert.Equal("https://store-images.s-microsoft.com/poster-big", product.ImageUrl("Poster"));
        }

        [Fact]
        public void FallsBackToNextImagePurpose()
        {
            var product = Parse(PlayAnywhereGame);
            Assert.Equal("https://store-images.s-microsoft.com/logo", product.ImageUrl("SuperHeroArt", "Logo"));
            Assert.Null(product.ImageUrl("Screenshot"));
        }
    }
}
