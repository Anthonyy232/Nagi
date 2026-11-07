using Windows.Storage;
using Nagi.Core.Platform;

namespace Nagi.WinUI.Services.Implementations;

public sealed class ApplicationDataSettingsStore : ISettingsStore
{
    public IDictionary<string, object> Values => ApplicationData.Current.LocalSettings.Values;
}
