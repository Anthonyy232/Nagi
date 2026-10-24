using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
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
        services.AddHttpClient();
        services.AddSingleton(Substitute.For<IConfiguration>());
        services.AddSingleton(Substitute.For<IPathConfiguration>());
        services.AddSingleton(Substitute.For<ISettingsService>());
        services.AddSingleton(Substitute.For<IAppInfoService>());
        services.AddSingleton(Substitute.For<IAudioPlayer>());
        services.AddSingleton(Substitute.For<ISystemMediaControls>());
        services.AddNagiCore();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task AddNagiCore_ResolvesWithOnlyTheDocumentedHostServices()
    {
        var build = BuildProvider;

        await using var provider = build.Should().NotThrow().Subject;
    }

    [Fact]
    public async Task AddNagiCore_SharesOneLibraryServiceAcrossWriterAndScanner()
    {
        await using var provider = BuildProvider();

        var writer = provider.GetRequiredService<ILibraryWriter>();
        var scanner = provider.GetRequiredService<ILibraryScanner>();

        writer.Should().BeOfType<LibraryService>().And.BeSameAs(scanner);
        provider.GetRequiredService<ILibraryReader>().Should().BeOfType<LibraryReader>();
        provider.GetRequiredService<IPlaylistService>().Should().BeOfType<PlaylistService>();
    }

    [Fact]
    public async Task AddNagiCore_RegistersEveryListenSubmitterAndPresenceService()
    {
        await using var provider = BuildProvider();

        provider.GetServices<IListenSubmitter>().Should().HaveCount(2);
        provider.GetServices<IPresenceService>().Should().HaveCount(3);
    }
}
