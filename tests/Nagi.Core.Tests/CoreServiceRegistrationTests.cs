using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nagi.Core.Helpers;
using Nagi.Core.Http;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Services.Implementations.MusicServers;
using Nagi.Core.Services;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class CoreServiceRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IConfiguration>());
        services.AddSingleton(Substitute.For<IPathConfiguration>());
        services.AddSingleton(Substitute.For<ISettingsStore>());
        services.AddSingleton(Substitute.For<IAutoLaunchService>());
        services.AddSingleton(Substitute.For<IAppInfoService>());
        services.AddSingleton(Substitute.For<ICredentialLockerService>());
        services.AddSingleton(Substitute.For<IAudioPlayer>());
        services.AddSingleton(Substitute.For<ISystemMediaControls>());
        services.AddSingleton(Substitute.For<IDispatcherService>());
        services.AddNagiCore();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task AddNagiCore_SharesTheLibraryServicesTheirCollaboratorsUseDirectly()
    {
        await using var provider = BuildProvider();

        provider.GetRequiredService<ILibraryNotifier>().Should().BeSameAs(provider.GetRequiredService<LibraryNotifier>());
        provider.GetRequiredService<IArtworkService>().Should().BeSameAs(provider.GetRequiredService<ArtworkService>());
        provider.GetRequiredService<IPlaylistService>().Should().BeSameAs(provider.GetRequiredService<PlaylistService>());
    }

    [Fact]
    public async Task AddNagiCore_RegistersEveryListenSubmitterAndPresenceService()
    {
        await using var provider = BuildProvider();

        provider.GetServices<IListenSubmitter>().Should().HaveCount(2);
        provider.GetServices<IPresenceService>().Should().HaveCount(4);
    }

    [Theory]
    [InlineData("")]
    [InlineData(ArtworkService.ImageHttpClientName)]
    [InlineData(MusicServerService.HttpClientName)]
    public async Task AddNagiCore_IdentifiesNagiOnceOnEveryHttpClient(string clientName)
    {
        await using var provider = BuildProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        client.DefaultRequestHeaders.UserAgent.ToString().Should().Be(ProviderUserAgent.Value);
    }
}
