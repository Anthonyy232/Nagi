using Windows.Storage;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

public sealed class ApplicationDataSettingsStore : ISettingsStore
{
    public IDictionary<string, object> Values => ApplicationData.Current.LocalSettings.Values;
}
