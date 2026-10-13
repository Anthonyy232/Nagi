using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Nagi.Core.Services;
using Nagi.Presentation.Services;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Popups;
using Nagi.WinUI.Services;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public class AppServiceRegistrationTests
{
    private static ServiceProvider BuildProvider()
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
        services.AddNagiPresentation();
        services.AddNagiShell();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task AppRegistrations_ResolveWithOnlyTheMainWindowAndConfiguration()
    {
        var build = BuildProvider;

        await using var provider = build.Should().NotThrow().Subject;
    }

    [Theory]
    [InlineData(typeof(MainPage))]
    [InlineData(typeof(OnboardingPage))]
    [InlineData(typeof(MiniPlayerWindow))]
    [InlineData(typeof(TrayPopup))]
    public async Task ShellFactories_CanSupplyEveryConstructorDependency(Type shellType)
    {
        await using var provider = BuildProvider();
        var isService = provider.GetRequiredService<IServiceProviderIsService>();

        // The tray popup's theme is passed in by TrayPopupService when it creates the popup.
        var dependencies = shellType.GetConstructors().Should().ContainSingle().Subject.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Where(type => type != typeof(ElementTheme));

        dependencies.Should().OnlyContain(type => isService.IsService(type));
    }
}
