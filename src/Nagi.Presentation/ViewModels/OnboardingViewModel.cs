using System;
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
///     Walks a new user through choosing a music folder and the settings they are most likely to want.
///     The library scan starts as soon as a folder is chosen and runs while the remaining steps are shown.
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
    private readonly ILogger<OnboardingViewModel> _logger;
    private readonly IUIService _uiService;

    public OnboardingViewModel(ILibraryWriter libraryWriter, ILibraryScanner libraryScanner, IUIService uiService,
        IApplicationLifecycle applicationLifecycle, ILogger<OnboardingViewModel> logger)
    {
        _libraryWriter = libraryWriter;
        _libraryScanner = libraryScanner;
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
        nameof(HasLibraryFolder))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand), nameof(GoNextCommand), nameof(GoToStepCommand),
        nameof(SkipSetupCommand), nameof(ChooseFolderCommand), nameof(FinishCommand))]
    private partial OnboardingLibraryState LibraryState { get; set; }

    [ObservableProperty] public partial string? FolderPath { get; private set; }

    [ObservableProperty] public partial string ScanStatus { get; private set; } = string.Empty;

    [ObservableProperty] public partial double ScanPercentage { get; private set; }

    [ObservableProperty] public partial bool IsScanProgressIndeterminate { get; private set; }

    public bool IsScanning => LibraryState == OnboardingLibraryState.Scanning;
    public bool IsLibraryReady => LibraryState == OnboardingLibraryState.Ready;
    public bool HasLibraryFailed => LibraryState == OnboardingLibraryState.Failed;
    public bool HasLibraryFolder => LibraryState is OnboardingLibraryState.Scanning or OnboardingLibraryState.Ready;

    private bool CanGoTo(OnboardingStep step)
    {
        if (step == CurrentStep || !Enum.IsDefined(step)) return false;
        // Changing the language on the welcome step restarts the app, which would abandon a running scan.
        if (step == OnboardingStep.Welcome) return !HasLibraryFolder;
        // After a failed scan the remaining steps stay open; the finish step offers choosing another folder.
        return step <= OnboardingStep.Library || FolderPath is not null;
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

    private bool CanChooseFolder() => !HasLibraryFolder;

    // Completes when the scan does, which runs while the user moves on through the remaining steps.
    [RelayCommand(CanExecute = nameof(CanChooseFolder))]
    private async Task ChooseFolderAsync()
    {
        var folderPath = await _uiService.PickSingleFolderAsync();
        if (folderPath is null) return;

        _logger.LogDebug("User selected folder '{FolderPath}' for onboarding", folderPath);
        FolderPath = folderPath;
        ScanStatus = Strings.Onboarding_BuildingLibrary;
        ScanPercentage = 0;
        IsScanProgressIndeterminate = true;
        // Moving on before the folder prompt hides lets focus on it follow to the next step instead of being lost.
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
            if (await _libraryWriter.AddFolderAsync(folderPath) is { } folder)
            {
                await _libraryScanner.ScanFolderForMusicAsync(folder.Path, progress);
                _logger.LogInformation("Onboarding library scan complete");
                LibraryState = OnboardingLibraryState.Ready;
                ScanStatus = Strings.Onboarding_LibraryReady;
                return;
            }

            _logger.LogWarning("Could not add onboarding folder '{FolderPath}'", folderPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Onboarding library scan failed");
        }

        LibraryState = OnboardingLibraryState.Failed;
        ScanStatus = Strings.Onboarding_Error;
    }

    [RelayCommand(CanExecute = nameof(IsLibraryReady))]
    private Task FinishAsync() => _applicationLifecycle.NavigateToMainContentAsync();
}
