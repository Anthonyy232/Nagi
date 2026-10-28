using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class ScrobbleQueueCancellationTests
{
    [Theory]
    [InlineData(ServiceProviderIds.LastFm, 1)]
    [InlineData(ServiceProviderIds.ListenBrainz, 100)]
    public async Task Cancellation_StopsInFlightRequestAndPreservesAcknowledgedListens(string provider, int batchSize)
    {
        using var db = new DbContextFactoryTestHelper();
        using var handler = new TestHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        await using var pipeline = TestProviderPipeline.Build(provider);
        using var cancellation = new CancellationTokenSource();
        var settings = Substitute.For<ISettingsService>();
        settings.GetLastFmScrobblingEnabledAsync().Returns(true);
        settings.GetLastFmCredentialsAsync().Returns(((string?, string?)?)("user", "session"));
        settings.GetListenBrainzScrobblingEnabledAsync().Returns(true);
        settings.GetListenBrainzUserTokenAsync().Returns("token");
        settings.GetListenBrainzEnabledSinceUtcAsync().Returns(DateTime.UtcNow.AddDays(-1));
        var keys = Substitute.For<IApiKeyService>();
        keys.GetApiKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("key");
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        await using (var context = db.ContextFactory.CreateDbContext())
        {
            var song = new Song
            {
                Title = "Song",
                FilePath = "C:/Music/song.mp3",
                Folder = new Folder { Name = "Music", Path = "C:/Music" }
            };
            song.SongArtists.Add(new SongArtist { Artist = new Artist { Name = "Artist" } });
            song.SyncDenormalizedFields();
            context.Songs.Add(song);
            context.ListenHistory.AddRange(Enumerable.Range(1, batchSize + 1).Select(id => new ListenHistory
            {
                Id = id,
                Song = song,
                IsEligibleForScrobbling = true,
                ListenTimestampUtc = DateTime.UtcNow.AddMinutes(-batchSize - 2).AddSeconds(id)
            }));
            await context.SaveChangesAsync();
        }

        IListenSubmitter submitter = provider == ServiceProviderIds.LastFm
            ? new LastFmScrobblerService(httpFactory, pipeline, keys, settings, db.ContextFactory,
                NullLogger<LastFmScrobblerService>.Instance)
            : new ListenBrainzScrobblerService(httpFactory, pipeline, db.ContextFactory, settings,
                Substitute.For<IAppInfoService>(), NullLogger<ListenBrainzScrobblerService>.Instance);
        var successBody = provider == ServiceProviderIds.LastFm ? "<lfm status='ok'><scrobbles accepted='1' ignored='0'><scrobble><ignoredMessage code='0'/></scrobble></scrobbles></lfm>" : "{}";
        handler.RespondSequence((HttpStatusCode.OK, successBody));
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.SendAsyncFunc = async (_, ct) =>
        {
            requestStarted.SetResult();
            await releaseRequest.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(successBody) };
        };

        var processing = submitter.ProcessPendingListensAsync(cancellation.Token);
        try
        {
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(2)));

            await using var context = db.ContextFactory.CreateDbContext();
            var listens = await context.ListenHistory.AsNoTracking().OrderBy(listen => listen.Id).ToListAsync();
            var submitted = listens.Select(listen => provider == ServiceProviderIds.LastFm
                ? listen.IsScrobbled : listen.IsSubmittedToListenBrainz).ToArray();
            submitted.Take(batchSize).Should().OnlyContain(value => value);
            submitted.Last().Should().BeFalse();

            handler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(successBody)
            });
            await submitter.ProcessPendingListensAsync(CancellationToken.None);
            handler.Requests.Should().HaveCount(3);
            (await context.ListenHistory.AsNoTracking().AllAsync(listen => provider == ServiceProviderIds.LastFm
                ? listen.IsScrobbled : listen.IsSubmittedToListenBrainz)).Should().BeTrue();
        }
        finally
        {
            releaseRequest.TrySetResult();
            try { await processing; }
            catch (OperationCanceledException) { }
        }
    }
}
