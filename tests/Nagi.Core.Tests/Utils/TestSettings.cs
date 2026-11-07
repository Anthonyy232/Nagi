using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Settings;
using NSubstitute;

namespace Nagi.Core.Tests.Utils;

/// <summary>
///     The real settings service over an in-memory store, so tests set and read settings as the app does.
/// </summary>
public static class TestSettings
{
    public static SettingsService Create(IDictionary<string, object>? values = null,
        ICredentialLockerService? credentials = null, IAutoLaunchService? autoLaunch = null,
        IPlaybackStateStore? playbackStateStore = null)
    {
        var store = Substitute.For<ISettingsStore>();
        store.Values.Returns(values ?? new Dictionary<string, object>());
        return new SettingsService(store, Setting.DeclaredIn(typeof(CoreSettings)),
            playbackStateStore ?? Substitute.For<IPlaybackStateStore>(),
            credentials ?? Substitute.For<ICredentialLockerService>(),
            autoLaunch ?? Substitute.For<IAutoLaunchService>(), NullLogger<SettingsService>.Instance);
    }

    /// <summary>
    ///     Enables only the providers in <paramref name="ids" />, ranked in that order above the disabled ones.
    /// </summary>
    public static void EnableOnly(this ISettingsService settings, Setting<List<ServiceProviderSetting>> providers,
        params string[] ids) =>
        settings.Set(providers, providers.Default
            .OrderBy(provider => Array.IndexOf(ids, provider.Id) is var index and >= 0 ? index : ids.Length)
            .Select((provider, order) =>
            {
                provider.IsEnabled = ids.Contains(provider.Id);
                provider.Order = order;
                return provider;
            })
            .ToList());
}
