using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Platform;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Settings;

namespace Nagi.Presentation.Shell;

/// <summary>
///     Asks a returning user, once, to rate Nagi where they got it: the Store, or a star on GitHub. Either answer
///     ends it for good.
/// </summary>
public partial class SupportPromptViewModel : ObservableObject
{
    public const int LaunchesBeforeAsking = 5;

    private readonly ISettingsService _settings;
    private readonly IUIService _uiService;
    private readonly Uri _page;

    public SupportPromptViewModel(ISettingsService settings, IUIService uiService, IAppInfoService appInfo)
    {
        _settings = settings;
        _uiService = uiService;

        var isStoreInstall = appInfo.IsStoreInstall;
        _page = new Uri(isStoreInstall ? AppLinks.StoreReview : AppLinks.Repository);
        Message = isStoreInstall ? Strings.SupportPrompt_Message_Store : Strings.SupportPrompt_Message_GitHub;
        ActionText = isStoreInstall ? Strings.SupportPrompt_Action_Store : Strings.SupportPrompt_Action_GitHub;

        var launches = settings.Get(AppSettings.SupportPromptLaunches);
        if (launches < LaunchesBeforeAsking) settings.Set(AppSettings.SupportPromptLaunches, ++launches);
        IsOpen = launches >= LaunchesBeforeAsking && !settings.Get(AppSettings.SupportPromptAnswered);
    }

    public string Message { get; }
    public string ActionText { get; }

    [ObservableProperty] public partial bool IsOpen { get; private set; }

    [RelayCommand]
    private async Task OpenPageAsync()
    {
        Dismiss();
        await _uiService.OpenUriAsync(_page);
    }

    [RelayCommand]
    private void Dismiss()
    {
        IsOpen = false;
        _settings.Set(AppSettings.SupportPromptAnswered, true);
    }
}
