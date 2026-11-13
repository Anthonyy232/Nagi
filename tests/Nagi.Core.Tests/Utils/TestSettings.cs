using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.OnlineProviders;
using Nagi.Core.Platform;
using Nagi.Core.Scrobbling;
using Nagi.Core.Settings;
using NSubstitute;

namespace Nagi.Core.Tests.Utils;

/// <summary>
///     The real settings service over an in-memory store, so tests set and read settings as the app does.
/// </summary>
public static class TestSettings
{
    public static SettingsService Create(IDictionary<string, object>? values = null)
    {
        var store = Substitute.For<ISettingsStore>();
        store.Values.Returns(values ?? new Dictionary<string, object>());
        return new SettingsService(store, Setting.DeclaredIn(typeof(CoreSettings)), NullLogger<SettingsService>.Instance);
    }

    /// <summary>
    ///     The real scrobbling accounts, over a credential store that accepts every sign-in unless one is given.
    /// </summary>
    public static ScrobblingAccounts Accounts(ISettingsService settings, ICredentialLockerService? credentials = null,
        ILastFmAuthService? lastFmAuth = null) =>
        new(settings, credentials ?? AcceptingCredentials(), lastFmAuth ?? Substitute.For<ILastFmAuthService>(),
            NullLogger<ScrobblingAccounts>.Instance);

    private static ICredentialLockerService AcceptingCredentials()
    {
        var credentials = Substitute.For<ICredentialLockerService>();
        credentials.SaveCredential(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        return credentials;
    }

    /// <summary>
    ///     Enables only the providers in <paramref name="ids" />, ranked in that order above the disabled ones.
    /// </summary>
    public static void EnableOnly(this ISettingsService settings, Setting<List<ServiceProviderSetting>> providers,
        params string[] ids) =>
        settings.Set(providers, providers.Default
            .OrderBy(provider => Array.IndexOf(ids, provider.Id) is var index and >= 0 ? index : ids.Length)
            .Select(provider => provider with { IsEnabled = ids.Contains(provider.Id) })
            .ToList());
}
