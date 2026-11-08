using Xunit;

namespace XboxOwnedLibrary.Tests
{
    public class CleanNameTests
    {
        [Theory]
        [InlineData("Fallout 76 (PC)", "Fallout 76")]
        [InlineData("Wolfenstein: Youngblood (PC)", "Wolfenstein: Youngblood")]
        [InlineData("Minecraft Legends - Windows", "Minecraft Legends")]
        [InlineData("Forza Horizon 4 for Windows 10", "Forza Horizon 4")]
        [InlineData("Batman™: Arkham Knight", "Batman: Arkham Knight")]
        [InlineData("LEGO® Star Wars™: The Skywalker Saga", "LEGO Star Wars: The Skywalker Saga")]
        [InlineData("Halo", "Halo")]
        public void StripsStoreNoise(string input, string expected)
        {
            Assert.Equal(expected, OwnedGamesSync.CleanName(input));
        }

        [Fact]
        public void KeepsWindowsInsideTitle()
        {
            Assert.Equal("Windows Defender Simulator", OwnedGamesSync.CleanName("Windows Defender Simulator"));
        }
    }
}
