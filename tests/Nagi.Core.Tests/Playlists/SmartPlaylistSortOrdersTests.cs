using FluentAssertions;
using Nagi.Core.Library;
using Nagi.Core.Playlists;
using Xunit;

namespace Nagi.Core.Tests.Playlists;

public class SmartPlaylistSortOrdersTests
{
    [Fact]
    public void SmartPlaylistSortOrders_MapToEquivalentSongSortOrders()
    {
        foreach (var smartPlaylistSortOrder in Enum.GetValues<SmartPlaylistSortOrder>())
        {
            var songSortOrder = smartPlaylistSortOrder.ToSongSortOrder();

            songSortOrder.ToString().Should().Be(smartPlaylistSortOrder.ToString());
        }
    }

    [Fact]
    public void SongSortOrders_MapToEquivalentSmartPlaylistSortOrders()
    {
        foreach (var songSortOrder in Enum.GetValues<SongSortOrder>().Where(value => value != SongSortOrder.PlaylistOrder))
        {
            var smartPlaylistSortOrder = songSortOrder.ToSmartPlaylistSortOrder();

            smartPlaylistSortOrder.ToString().Should().Be(songSortOrder.ToString());
        }
    }

    [Fact]
    public void PlaylistOrder_MapsToTitleAscendingFallback()
    {
        SongSortOrder.PlaylistOrder.ToSmartPlaylistSortOrder()
            .Should().Be(SmartPlaylistSortOrder.TitleAsc);
    }
}
