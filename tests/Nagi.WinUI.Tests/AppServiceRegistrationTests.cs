using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        var dependencies = shellType.GetConstructors().Should().ContainSingle().Subject.GetParameters()
            .Select(parameter => parameter.ParameterType);

        dependencies.Should().OnlyContain(type => isService.IsService(type));
    }
}
