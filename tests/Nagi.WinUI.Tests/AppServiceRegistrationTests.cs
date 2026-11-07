using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Popups;
using Nagi.WinUI.Services;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public class AppServiceRegistrationTests
{
    // Validation checks every registration's dependencies without creating the window or anything else.
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection().AddNagiApp(Substitute.For<IConfiguration>())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

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
