using FluentAssertions;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Xunit;

namespace Nagi.Core.Tests;

public class SmartPlaylistSortOrderMappingTests
{
    [Fact]
    public void SmartPlaylistSortOrders_MapToEquivalentSongSortOrders()
    {
        foreach (var smartPlaylistSortOrder in Enum.GetValues<SmartPlaylistSortOrder>())
            smartPlaylistSortOrder.ToSongSortOrder().ToString().Should().Be(smartPlaylistSortOrder.ToString());
    }

    [Fact]
    public void SongSortOrders_MapToEquivalentSmartPlaylistSortOrders()
    {
        foreach (var songSortOrder in Enum.GetValues<SongSortOrder>().Where(value => value != SongSortOrder.PlaylistOrder))
            songSortOrder.ToSmartPlaylistSortOrder().ToString().Should().Be(songSortOrder.ToString());
    }

    [Fact]
    public void PlaylistOrder_MapsToTitleAscendingFallback()
    {
        SongSortOrder.PlaylistOrder.ToSmartPlaylistSortOrder().Should().Be(SmartPlaylistSortOrder.TitleAsc);
    }
}
