using FluentAssertions;
using Nagi.Core.Models;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Contains unit tests for the <see cref="ServiceProviderSetting" /> model and related functionality.
/// </summary>
public class ServiceProviderSettingTests
{
    [Fact]
    public void ServiceProviderSetting_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var setting = new ServiceProviderSetting();

        // Assert
        setting.Id.Should().Be(string.Empty);
        setting.DisplayName.Should().Be(string.Empty);
        setting.Category.Should().Be(ServiceCategory.Lyrics);
        setting.IsEnabled.Should().BeTrue();
        setting.Order.Should().Be(0);
        setting.Description.Should().BeNull();
    }

    [Fact]
    public void ServiceProviderSetting_PropertiesCanBeSet()
    {
        // Arrange & Act
        var setting = new ServiceProviderSetting
        {
            Id = "lrclib",
            DisplayName = "LRCLIB",
            Category = ServiceCategory.Lyrics,
            IsEnabled = false,
            Order = 5,
            Description = "Test description"
        };

        // Assert
        setting.Id.Should().Be("lrclib");
        setting.DisplayName.Should().Be("LRCLIB");
        setting.Category.Should().Be(ServiceCategory.Lyrics);
        setting.IsEnabled.Should().BeFalse();
        setting.Order.Should().Be(5);
        setting.Description.Should().Be("Test description");
    }

    [Theory]
    [InlineData(ServiceCategory.Lyrics)]
    [InlineData(ServiceCategory.Metadata)]
    public void ServiceCategory_AllValuesAreDefined(ServiceCategory category)
    {
        // Assert - verify the enum values exist and can be used
        category.Should().BeDefined();
    }

    [Fact]
    public void ServiceProviderSetting_CanBeUsedForLyricsCategory()
    {
        var settings = new List<ServiceProviderSetting>
        {
            new() { Id = "lrclib", DisplayName = "LRCLIB", Category = ServiceCategory.Lyrics, Order = 0 },
            new() { Id = "netease", DisplayName = "NetEase", Category = ServiceCategory.Lyrics, Order = 1 }
        };

        settings.Should().HaveCount(2);
        settings.All(s => s.Category == ServiceCategory.Lyrics).Should().BeTrue();
    }

    [Fact]
    public void ServiceProviderSetting_CanBeUsedForMetadataCategory()
    {
        var settings = new List<ServiceProviderSetting>
        {
            new() { Id = "musicbrainz", DisplayName = "MusicBrainz", Category = ServiceCategory.Metadata, Order = 0 },
            new() { Id = "lastfm", DisplayName = "Last.fm", Category = ServiceCategory.Metadata, Order = 1 }
        };

        settings.Should().HaveCount(2);
        settings.All(s => s.Category == ServiceCategory.Metadata).Should().BeTrue();
    }

    [Fact]
    public void ServiceProviderSetting_OrderingWorks()
    {
        // Arrange
        var settings = new List<ServiceProviderSetting>
        {
            new() { Id = "third", Order = 2 },
            new() { Id = "first", Order = 0 },
            new() { Id = "second", Order = 1 }
        };

        // Act
        var ordered = settings.OrderBy(s => s.Order).ToList();

        // Assert
        ordered[0].Id.Should().Be("first");
        ordered[1].Id.Should().Be("second");
        ordered[2].Id.Should().Be("third");
    }

    [Fact]
    public void ServiceProviderSetting_FilteringByEnabled()
    {
        // Arrange
        var settings = new List<ServiceProviderSetting>
        {
            new() { Id = "enabled1", IsEnabled = true },
            new() { Id = "disabled", IsEnabled = false },
            new() { Id = "enabled2", IsEnabled = true }
        };

        // Act
        var enabled = settings.Where(s => s.IsEnabled).ToList();

        // Assert
        enabled.Should().HaveCount(2);
        enabled.Select(s => s.Id).Should().Contain("enabled1", "enabled2");
        enabled.Select(s => s.Id).Should().NotContain("disabled");
    }
}
