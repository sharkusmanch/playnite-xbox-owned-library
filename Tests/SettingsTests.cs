using Newtonsoft.Json;
using Xunit;

namespace XboxOwnedLibrary.Tests
{
    public class SettingsTests
    {
        [Fact]
        public void OldTagSettingsCarryIntoFeatureSettings()
        {
            var json = @"{ ""AddPlayAnywhereTag"": true, ""PlayAnywhereTagName"": ""XPA"" }";
            var settings = JsonConvert.DeserializeObject<XboxOwnedLibrarySettings>(json);
            Assert.True(settings.AddPlayAnywhereFeature);
            Assert.Equal("XPA", settings.PlayAnywhereFeatureName);
        }

        [Fact]
        public void FeatureSettingsRoundTrip()
        {
            var json = JsonConvert.SerializeObject(new XboxOwnedLibrarySettings { AddPlayAnywhereFeature = true, PlayAnywhereFeatureName = "XPA" });
            var settings = JsonConvert.DeserializeObject<XboxOwnedLibrarySettings>(json);
            Assert.True(settings.AddPlayAnywhereFeature);
            Assert.Equal("XPA", settings.PlayAnywhereFeatureName);
        }

        [Fact]
        public void OldTagSettingOffDoesNotTurnFeatureOff()
        {
            var json = @"{ ""AddPlayAnywhereFeature"": true, ""AddPlayAnywhereTag"": null }";
            Assert.True(JsonConvert.DeserializeObject<XboxOwnedLibrarySettings>(json).AddPlayAnywhereFeature);
        }
    }
}
