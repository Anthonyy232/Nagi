using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.ViewModels;

public enum OnboardingStep
{
    Welcome,
    Library,
    Appearance,
    Listening,
    OnlineFeatures,
    Desktop,
    Sharing,
    Finish
}

/// <summary>
///     Walks a new user through choosing a music folder or server and the settings they are most likely to want.
///     Building the library starts as soon as one is chosen and runs while the remaining steps are shown.
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private enum OnboardingLibraryState
    {
        NotStarted,
        Scanning,
        Ready,
        Failed
    }

    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ILibraryScanner _libraryScanner;
    private readonly ILibraryWriter _libraryWriter;
    private readonly IMusicServerService _musicServers;
    private readonly ILogger<OnboardingViewModel> _logger;
    private readonly IUIService _uiService;

    public OnboardingViewModel(ILibraryWriter libraryWriter, ILibraryScanner libraryScanner,
        IMusicServerService musicServers, IUIService uiService, IApplicationLifecycle applicationLifecycle,
        ILogger<OnboardingViewModel> logger)
    {
        _libraryWriter = libraryWriter;
        _libraryScanner = libraryScanner;
        _musicServers = musicServers;
        _uiService = uiService;
        _applicationLifecycle = applicationLifecycle;
        _logger = logger;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnSettingsStep), nameof(IsOnFinishStep))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand), nameof(GoNextCommand), nameof(GoToStepCommand),
        nameof(SkipSetupCommand))]
    public partial OnboardingStep CurrentStep { get; private set; }

    public bool IsOnSettingsStep => CurrentStep is > OnboardingStep.Library and < OnboardingStep.Finish;
    public bool IsOnFinishStep => CurrentStep == OnboardingStep.Finish;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanning), nameof(IsLibraryReady), nameof(HasLibraryFailed),
        nameof(HasLibrarySource))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand), nameof(GoNextCommand), nameof(GoToStepCommand),
        nameof(SkipSetupCommand), nameof(ChooseFolderCommand), nameof(ConnectServerCommand), nameof(FinishCommand))]
    private partial OnboardingLibraryState LibraryState { get; set; }

    /// <summary>
    ///     The folder path, or the music server's name, that the library is built from.
    /// </summary>
    [ObservableProperty] public partial string? LibrarySource { get; private set; }

    [ObservableProperty] public partial string ScanStatus { get; private set; } = string.Empty;

    [ObservableProperty] public partial double ScanPercentage { get; private set; }

    [ObservableProperty] public partial bool IsScanProgressIndeterminate { get; private set; }

    public bool IsScanning => LibraryState == OnboardingLibraryState.Scanning;
    public bool IsLibraryReady => LibraryState == OnboardingLibraryState.Ready;
    public bool HasLibraryFailed => LibraryState == OnboardingLibraryState.Failed;
    public bool HasLibrarySource => LibraryState is OnboardingLibraryState.Scanning or OnboardingLibraryState.Ready;

    private bool CanGoTo(OnboardingStep step)
    {
        if (step == CurrentStep || !Enum.IsDefined(step)) return false;
        // Changing the language on the welcome step restarts the app, which would abandon a running scan.
        if (step == OnboardingStep.Welcome) return !HasLibrarySource;
        // After a failed scan the remaining steps stay open; the finish step offers choosing another folder or server.
        return step <= OnboardingStep.Library || LibrarySource is not null;
    }

    private bool CanGoBack() => CanGoTo(CurrentStep - 1);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => CurrentStep--;

    private bool CanGoNext() => CanGoTo(CurrentStep + 1);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void GoNext() => CurrentStep++;

    [RelayCommand(CanExecute = nameof(CanGoTo))]
    private void GoToStep(OnboardingStep step) => CurrentStep = step;

    private bool CanSkipSetup() => CanGoTo(OnboardingStep.Finish);

    [RelayCommand(CanExecute = nameof(CanSkipSetup))]
    private void SkipSetup() => CurrentStep = OnboardingStep.Finish;

    private bool CanChooseLibrary() => !HasLibrarySource;

    [RelayCommand(CanExecute = nameof(CanChooseLibrary))]
    private async Task ChooseFolderAsync()
    {
        var folderPath = await _uiService.PickSingleFolderAsync();
        if (folderPath is null) return;

        _logger.LogDebug("User selected folder '{FolderPath}' for onboarding", folderPath);
        await BuildLibraryAsync(folderPath, async progress =>
        {
            if (await _libraryWriter.AddFolderAsync(folderPath) is not { } folder)
            {
                _logger.LogWarning("Could not add onboarding folder '{FolderPath}'", folderPath);
                return false;
            }

            await _libraryScanner.ScanFolderForMusicAsync(folder.Path, progress);
            return true;
        });
    }

    [RelayCommand(CanExecute = nameof(CanChooseLibrary))]
    private async Task ConnectServerAsync()
    {
        if (await _uiService.ShowAddMusicServerDialogAsync() is not { } server) return;

        await BuildLibraryAsync(server.Name, async progress =>
        {
            try
            {
                await _musicServers.SyncAsync(server.Id, progress);
            }
            catch
            {
                // Setup can't finish with an empty server, and connecting to it again would be refused as already added.
                await _libraryWriter.RemoveFolderAsync(server.Id);
                throw;
            }

            return true;
        });
    }

    // Completes when the scan does, which runs while the user moves on through the remaining steps.
    private async Task BuildLibraryAsync(string source, Func<IProgress<ScanProgress>, Task<bool>> buildAsync)
    {
        LibrarySource = source;
        ScanStatus = Strings.Onboarding_BuildingLibrary;
        ScanPercentage = 0;
        IsScanProgressIndeterminate = true;
        // Moving on before the library prompt hides lets focus on it follow to the next step instead of being lost.
        if (CurrentStep == OnboardingStep.Library) CurrentStep++;
        LibraryState = OnboardingLibraryState.Scanning;

        var progress = new Progress<ScanProgress>(report =>
        {
            // Progress callbacks are posted, so one can arrive after the scan has already finished.
            if (!IsScanning) return;
            ScanStatus = report.StatusText;
            ScanPercentage = report.Percentage;
            IsScanProgressIndeterminate = report.IsIndeterminate;
        });

        try
        {
            if (await buildAsync(progress))
            {
                _logger.LogInformation("Onboarding library scan complete");
                LibraryState = OnboardingLibraryState.Ready;
                ScanStatus = Strings.Onboarding_LibraryReady;
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding library scan failed");
        }

        LibraryState = OnboardingLibraryState.Failed;
        ScanStatus = Strings.Onboarding_Error;
    }

    [RelayCommand(CanExecute = nameof(IsLibraryReady))]
    private async Task FinishAsync()
    {
        try
        {
            await _applicationLifecycle.NavigateToMainContentAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open the library after onboarding");
            await _uiService.ShowMessageDialogAsync(Strings.Generic_Error, Strings.Listening_ActionError);
        }
    }
}
