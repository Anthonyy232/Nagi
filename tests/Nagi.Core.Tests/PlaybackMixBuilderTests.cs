using FluentAssertions;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Xunit;

namespace Nagi.Core.Tests;

public class PlaybackMixBuilderTests
{
    [Fact]
    public void Variety_SpacesArtistsAndExcludesRecentTracksWithoutLosingEligibleTracks()
    {
        var now = DateTime.UtcNow;
        var songs = Enumerable.Range(0, 12).Select(i => new MixCandidate(Guid.NewGuid(), $"Artist {i % 3}",
            TimeSpan.FromMinutes(3).Ticks, 1, now.AddDays(-10))).ToArray();
        var recent = new MixCandidate(Guid.NewGuid(), "Recent", 1, 1, now.AddHours(-1));

        var result = PlaybackMixBuilder.Build(songs.Append(recent).Concat(songs), ShuffleMode.MoreVariety, now, random: new Random(2));

        result.Should().BeEquivalentTo(songs.Select(s => s.Id));
        var artists = result.Select(id => songs.Single(s => s.Id == id).Artist).ToArray();
        artists.Zip(artists.Skip(1)).Should().OnlyContain(pair => pair.First != pair.Second);
    }

    [Theory]
    [InlineData(ShuffleMode.Unheard, 0)]
    [InlineData(ShuffleMode.Rediscover, 1)]
    public void Discovery_SelectsOnlyTracksSupportedByHistory(ShuffleMode mode, int expected)
    {
        var now = DateTime.UtcNow;
        MixCandidate[] songs = [
            new(Guid.NewGuid(), "Artist", 1, 0, null),
            new(Guid.NewGuid(), "Artist", 1, 4, now.AddDays(-60)),
            new(Guid.NewGuid(), "Artist", 1, 4, now.AddDays(-5)),
            new(Guid.NewGuid(), "Artist", 1, 4, null)
        ];

        PlaybackMixBuilder.Build(songs, mode, now).Should().Equal(songs[expected].Id);
    }

    [Fact]
    public void TimedMix_SkipsUnknownAndOversizedTracksAndUsesWholeSongsWithinBudget()
    {
        MixCandidate[] songs = [
            new(Guid.NewGuid(), "A", 0, 0, null),
            new(Guid.NewGuid(), "A", -1, 0, null),
            new(Guid.NewGuid(), "A", TimeSpan.FromMinutes(20).Ticks, 0, null),
            new(Guid.NewGuid(), "A", TimeSpan.FromMinutes(3).Ticks, 0, null),
            new(Guid.NewGuid(), "B", TimeSpan.FromMinutes(2).Ticks, 0, null)
        ];

        PlaybackMixBuilder.Build(songs, ShuffleMode.Random, DateTime.UtcNow, TimeSpan.FromMinutes(5))
            .Should().BeEquivalentTo(songs.Skip(3).Select(s => s.Id));
    }
}
