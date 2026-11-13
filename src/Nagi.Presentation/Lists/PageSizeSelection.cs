using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Settings;

namespace Nagi.Presentation.Lists;

/// <summary>
///     The page size every pager shares: the sizes it offers and the saved one. Choosing a size in one pager saves
///     it, and every list with a pager follows.
/// </summary>
public sealed class PageSizeSelection : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IDispatcherService _dispatcherService;

    public PageSizeSelection(ISettingsService settingsService, IDispatcherService dispatcherService)
    {
        _settingsService = settingsService;
        _dispatcherService = dispatcherService;
        _settingsService.SettingChanged += OnSettingChanged;
    }

    /// <summary>Raised on the UI thread after the saved size changed, whichever pager changed it.</summary>
    public event Action? Changed;

    public IReadOnlyList<SettingOption<int>> Options { get; } =
        SettingOption<int>.For(AppSettings.PageSizeOptions, SettingsText.GetPageSizeName);

    /// <summary>The items a page holds, or 0 to show every item.</summary>
    public int Size => _settingsService.Get(AppSettings.SongsPerPage);

    /// <summary>Null when the saved page size isn't one of the options.</summary>
    public SettingOption<int>? Selected
    {
        get => Options.FirstOrDefault(option => option.Value == Size);
        set
        {
            if (value is not null) _settingsService.Set(AppSettings.SongsPerPage, value.Value);
        }
    }

    /// <summary>Paging only means something once there are more items than the smallest page holds.</summary>
    public static bool IsUsefulFor(int totalItemCount) => totalItemCount > AppSettings.PageSizeOptions[0];

    public void Dispose() => _settingsService.SettingChanged -= OnSettingChanged;

    private void OnSettingChanged(Setting setting)
    {
        if (setting != AppSettings.SongsPerPage) return;
        _dispatcherService.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(Selected));
            Changed?.Invoke();
        });
    }
}
