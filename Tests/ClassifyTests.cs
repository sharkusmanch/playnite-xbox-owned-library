using XboxOwnedLibrary.Services;
using Xunit;

namespace XboxOwnedLibrary.Tests
{
    public class ClassifyTests
    {
        private static CollectionItem Game(string id = "9NBLGGH18846") =>
            new CollectionItem { productId = id, productKind = "Game", status = "Active", skuType = "Full" };

        [Fact]
        public void ActiveFullGameIsOwned()
        {
            Assert.Equal(OwnershipKind.Owned, OwnedLibraryClient.Classify(Game()));
        }

        [Fact]
        public void LicensingRequiredIsGamesWithGold()
        {
            var item = Game();
            item.licensingRequired = true;
            Assert.Equal(OwnershipKind.Gold, OwnedLibraryClient.Classify(item));
        }

        [Theory]
        [InlineData("Revoked")]
        [InlineData("Expired")]
        public void InactiveIsIgnored(string status)
        {
            var item = Game();
            item.status = status;
            Assert.Equal(OwnershipKind.Ignored, OwnedLibraryClient.Classify(item));
        }

        [Fact]
        public void TrialSkuIsIgnored()
        {
            var item = Game();
            item.skuType = "Trial";
            Assert.Equal(OwnershipKind.Ignored, OwnedLibraryClient.Classify(item));
        }

        [Fact]
        public void TrialFlagIsIgnored()
        {
            var item = Game();
            item.isTrial = true;
            Assert.Equal(OwnershipKind.Ignored, OwnedLibraryClient.Classify(item));
        }

        [Fact]
        public void SubscriptionProductIsIgnored()
        {
            Assert.Equal(OwnershipKind.Ignored, OwnedLibraryClient.Classify(Game("CFQ7TTC0KHS0")));
        }

        [Theory]
        [InlineData("Durable")]
        [InlineData("Consumable")]
        [InlineData("Movie")]
        [InlineData("Application")]
        public void NonGameKindIsIgnored(string kind)
        {
            var item = Game();
            item.productKind = kind;
            Assert.Equal(OwnershipKind.Ignored, OwnedLibraryClient.Classify(item));
        }
    }
}
