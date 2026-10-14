using System.Collections.Generic;
using Nagi.Presentation.Services.Abstractions;
using Windows.Storage;

namespace Nagi.WinUI.Services.Implementations;

public sealed class ApplicationDataSettingsStore : ISettingsStore
{
    public IDictionary<string, object> Values => ApplicationData.Current.LocalSettings.Values;
}
