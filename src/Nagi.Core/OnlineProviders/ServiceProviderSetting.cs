namespace Nagi.Core.OnlineProviders;

/// <summary>
///     Whether the user turned a provider on. Saved in a list in the user's priority order, highest first.
/// </summary>
/// <param name="Id">One of <see cref="ProviderCatalog" />'s provider ids.</param>
public sealed record ServiceProviderSetting(string Id, bool IsEnabled = true);
