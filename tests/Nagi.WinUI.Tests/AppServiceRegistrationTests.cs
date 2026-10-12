using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Nagi.Core.Services;
using Nagi.WinUI.Services;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public class AppServiceRegistrationTests
{
    [Fact]
    public async Task AppRegistrations_ResolveWithOnlyTheMainWindowAndConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IConfiguration>());
        // The window and its dispatcher exist only inside the running app; validation never creates them.
        services.AddSingleton<MainWindow>(_ => throw new NotSupportedException());
        services.AddSingleton<DispatcherQueue>(_ => throw new NotSupportedException());
        services.AddNagiHttpClients();
        services.AddNagiPlatformServices();
        services.AddNagiCore();
        services.AddNagiViewModels();

        var build = () => services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        await using var provider = build.Should().NotThrow().Subject;
    }
}
