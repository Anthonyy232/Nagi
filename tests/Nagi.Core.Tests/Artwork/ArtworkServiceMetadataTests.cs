using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Library;
using Nagi.Core.OnlineProviders;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Artwork;

public class ArtworkServiceMetadataTests : LibraryTestBase
{
    [Theory]
    [InlineData(ServiceProviderIds.LastFm)]
    [InlineData(ServiceProviderIds.TheAudioDb)]
    [InlineData(ServiceProviderIds.FanartTv)]
    [InlineData(ServiceProviderIds.MusicBrainz)]
    public async Task ArtistMetadata_TemporaryProviderFailure_RemainsRetryable(string providerId)
    {
        _settingsService.Set(CoreSettings.FetchOnlineMetadata, true);
        var artist = new Artist
        {
            Name = "Retry artist",
            MusicBrainzId = providerId == ServiceProviderIds.MusicBrainz ? null : Guid.NewGuid().ToString()
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        _settingsService.EnableOnly(CoreSettings.MetadataProviders, providerId);
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
            ServiceResult<ArtistInfo>.FromTemporaryError(), ServiceResult<ArtistInfo>.FromSuccessNotFound());
        _theAudioDbService.GetArtistMetadataAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(
            ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError(), ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound());
        _fanartTvService.GetArtistImagesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            ServiceResult<FanartTvArtistImages>.FromTemporaryError(), ServiceResult<FanartTvArtistImages>.FromSuccessNotFound());
        _musicBrainzService.SearchArtistAsync(artist.Name, Arg.Any<CancellationToken>()).Returns(
            ServiceResult<string>.FromTemporaryError(), ServiceResult<string>.FromSuccessNotFound());

        var failed = await _library.Artwork.GetArtistDetailsAsync(artist.Id);
        failed!.MetadataLastCheckedUtc.Should().BeNull();

        var retried = await _library.Artwork.GetArtistDetailsAsync(artist.Id);
        retried!.MetadataLastCheckedUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData(ServiceProviderIds.MusicBrainz)]
    [InlineData(ServiceProviderIds.CoverArtArchive, ServiceProviderIds.AppleMusic)]
    public async Task ArtistMetadata_WithoutAnEnabledSourceThatCanLookTheArtistUp_MarksTheArtistChecked(
        params string[] providerIds)
    {
        _settingsService.Set(CoreSettings.FetchOnlineMetadata, true);
        var artist = new Artist { Name = "Known artist", MusicBrainzId = Guid.NewGuid().ToString() };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        _settingsService.EnableOnly(CoreSettings.MetadataProviders, providerIds);

        var result = await _library.Artwork.GetArtistDetailsAsync(artist.Id);

        result!.MetadataLastCheckedUtc.Should().NotBeNull();
        await _musicBrainzService.DidNotReceive().SearchArtistAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ServiceProviderIds.TheAudioDb)]
    [InlineData(ServiceProviderIds.MusicBrainz)]
    public async Task ArtistMetadata_ProviderFailure_UsesLowerPriorityBiography(string providerId)
    {
        _settingsService.Set(CoreSettings.FetchOnlineMetadata, true);
        var artist = new Artist
        {
            Name = "Fallback artist",
            MusicBrainzId = providerId == ServiceProviderIds.MusicBrainz ? null : Guid.NewGuid().ToString()
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        _settingsService.EnableOnly(CoreSettings.MetadataProviders, providerId, ServiceProviderIds.LastFm);
        _theAudioDbService.GetArtistMetadataAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError());
        _musicBrainzService.SearchArtistAsync(artist.Name, Arg.Any<CancellationToken>())
            .Returns(ServiceResult<string>.FromTemporaryError());
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo { Biography = "Fallback biography" }));

        var result = await _library.Artwork.GetArtistDetailsAsync(artist.Id);

        result!.Biography.Should().Be("Fallback biography");
        result.MetadataLastCheckedUtc.Should().BeNull();
    }

    [Fact]
    public async Task ArtistMetadata_FailedImageDownload_RetriesWithoutLosingBiography()
    {
        _settingsService.Set(CoreSettings.FetchOnlineMetadata, true);
        var artist = new Artist { Name = "Image retry artist" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        _settingsService.EnableOnly(CoreSettings.MetadataProviders, ServiceProviderIds.LastFm);
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo
            {
                Biography = "Biography",
                ImageUrl = "https://example.com/artist.jpg"
            }));
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_httpMessageHandler, disposeHandler: false));
        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var failed = await _library.Artwork.GetArtistDetailsAsync(artist.Id);
        failed!.Biography.Should().Be("Biography");
        failed.LocalImageCachePath.Should().BeNull();
        failed.MetadataLastCheckedUtc.Should().BeNull();

        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        });
        _imageProcessor.ProcessImageBytesAsync(Arg.Any<byte[]>()).Returns(new byte[] { 4, 5, 6 });
        var retried = await _library.Artwork.GetArtistDetailsAsync(artist.Id);

        retried!.Biography.Should().Be("Biography");
        retried.LocalImageCachePath.Should().NotBeNullOrEmpty();
        retried.MetadataLastCheckedUtc.Should().NotBeNull();
        _fileSystem.Received(1).MoveFile(Arg.Is<string>(path => path.EndsWith(".tmp")), retried.LocalImageCachePath!, true);
        await _fileSystem.DidNotReceive().WriteAllBytesAsync(retried.LocalImageCachePath!, Arg.Any<byte[]>());
    }

    [Theory]
    [InlineData("dispose")]
    [InlineData("disable")]
    [InlineData("reset")]
    public async Task BackgroundMetadata_CancellationLetsActiveRunUnwind(string action)
    {
        _settingsService.Set(CoreSettings.FetchOnlineMetadata, true);
        var artist = new Artist { Name = "Pending artist" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        _settingsService.EnableOnly(CoreSettings.MetadataProviders, ServiceProviderIds.LastFm);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TaskCompletionSource<ServiceResult<ArtistInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            started.TrySetResult(call.Arg<CancellationToken>());
            return provider.Task;
        });

        var run = _library.Artwork.StartMetadataBackgroundFetchAsync();
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task reset = Task.CompletedTask;
        if (action == "dispose") _library.Dispose();
        else if (action == "disable") _settingsService.Set(CoreSettings.FetchOnlineMetadata, false);
        // The reset may finish at once: cancelling the run can unwind it, releasing the fetch, within that call.
        else reset = _library.Reset.ClearAllLibraryDataAsync();
        token.IsCancellationRequested.Should().BeTrue();
        provider.SetResult(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo { Biography = "Late result" }));
        await Task.WhenAll(run, reset).WaitAsync(TimeSpan.FromSeconds(5));

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var stored = await context.Artists.FindAsync(artist.Id);
            if (action == "reset") stored.Should().BeNull();
            else
            {
                stored!.Biography.Should().BeNull();
                stored.MetadataLastCheckedUtc.Should().BeNull();
            }
        }
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccessNotFound());
        _settingsService.Set(CoreSettings.FetchOnlineMetadata, true);
        await _library.Artwork.StartMetadataBackgroundFetchAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await _lastFmService.Received(action == "disable" ? 2 : 1)
            .GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BackgroundMetadataFetch_AttemptsEachArtistOncePerRunAndRetriesOnNextRun()
    {
        _settingsService.Set(CoreSettings.FetchOnlineMetadata, true);
        _settingsService.EnableOnly(CoreSettings.MetadataProviders, ServiceProviderIds.LastFm);
        _lastFmService.GetArtistInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromTemporaryError());
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.AddRange(Enumerable.Range(0, 51).Select(i => new Artist { Name = $"Artist {i}" }));
            await context.SaveChangesAsync();
        }

        await _library.Artwork.StartMetadataBackgroundFetchAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await _lastFmService.Received(51).GetArtistInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
            (await context.Artists.CountAsync(a => a.MetadataLastCheckedUtc == null)).Should().Be(51);

        _lastFmService.GetArtistInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccessNotFound());
        await _library.Artwork.StartMetadataBackgroundFetchAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await _lastFmService.Received(102).GetArtistInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Artists.CountAsync(a => a.MetadataLastCheckedUtc == null)).Should().Be(0);
    }

    [Fact]
    public async Task GetArtistDetailsAsync_WithMissingMetadataAndFetchAllowed_FetchesAndUpdatesArtist()
    {
        _settingsService.Set(CoreSettings.FetchOnlineMetadata, true);
        var artist = new Artist
        { Id = Guid.NewGuid(), Name = "Remote Artist", Biography = null, LocalImageCachePath = null };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }

        _settingsService.EnableOnly(CoreSettings.MetadataProviders, ServiceProviderIds.LastFm);

        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ServiceResult<ArtistInfo>.FromSuccess(new ArtistInfo
            { Biography = "A cool bio.", ImageUrl = "http://example.com/image.jpg" }));

        var artistCachePath = _pathConfig.ArtistImageCachePath;
        var artistImageFilename = $"{artist.Id}.fetched.jpg";
        var expectedImagePath = Path.Combine(artistCachePath, artistImageFilename);
        _fileSystem.FileExists(expectedImagePath).Returns(false);
        _imageProcessor.ProcessImageBytesAsync(Arg.Any<byte[]>()).Returns(new byte[] { 4, 5, 6 });

        _httpMessageHandler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        });

        var eventFired = false;
        _library.Notifier.ArtistImagesChanged += (_, changes) =>
        {
            eventFired = true;
            changes.Single().ArtistId.Should().Be(artist.Id);
            changes.Single().ImagePath.Should().Be(expectedImagePath);
        };

        var result = await _library.Artwork.GetArtistDetailsAsync(artist.Id);

        result.Should().NotBeNull();
        result!.Biography.Should().Be("A cool bio.");
        result.LocalImageCachePath.Should().Be(expectedImagePath);
        result.MetadataLastCheckedUtc.Should().NotBeNull();
        eventFired.Should().BeTrue();

        _fileSystem.Received(1).MoveFile(Arg.Is<string>(path => path.EndsWith(".tmp")), expectedImagePath, true);
    }

    [Fact]
    public async Task GetArtistDetailsAsync_WithOnlineMetadataOff_DoesNotCallRemoteServices()
    {
        _settingsService.Set(CoreSettings.FetchOnlineMetadata, false);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Local Artist", Biography = null };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }

        var result = await _library.Artwork.GetArtistDetailsAsync(artist.Id);

        result.Should().NotBeNull();
        result!.Biography.Should().BeNull();

        await _lastFmService.DidNotReceive().GetArtistInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
