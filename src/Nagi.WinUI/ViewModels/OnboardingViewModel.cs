using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

public partial class OnboardingViewModel : ObservableObject
{
    private static string InitialWelcomeMessage => Nagi.WinUI.Resources.Strings.Onboarding_Welcome;
    private readonly IApplicationLifecycle _applicationLifecycle;

    private readonly ILibraryService _libraryService;
    private readonly ILogger<OnboardingViewModel> _logger;
    private readonly IUIService _uiService;

    public OnboardingViewModel(ILibraryService libraryService, IUIService uiService,
        IApplicationLifecycle applicationLifecycle, ILogger<OnboardingViewModel> logger)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _applicationLifecycle = applicationLifecycle ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        _logger = logger;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsAddingFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsParsing { get; set; }

    [ObservableProperty] public partial string StatusMessage { get; set; } = InitialWelcomeMessage;

    [ObservableProperty] public partial double ProgressValue { get; set; }

    [ObservableProperty] public partial bool IsProgressIndeterminate { get; set; }

    public bool IsAnyOperationInProgress => IsAddingFolder || IsParsing;

    [RelayCommand]
    private async Task AddFolder()
    {
        if (IsAnyOperationInProgress) return;

        IsAddingFolder = true;
        StatusMessage = Nagi.WinUI.Resources.Strings.Onboarding_WaitingForSelection;

        try
        {
            var folderPath = await _uiService.PickSingleFolderAsync();

            if (folderPath != null)
            {
                _logger.LogDebug("User selected folder '{FolderPath}' for onboarding", folderPath);
                IsAddingFolder = false;
                IsParsing = true;
                StatusMessage = Nagi.WinUI.Resources.Strings.Onboarding_BuildingLibrary;
                IsProgressIndeterminate = true;

                var progressReporter = new Progress<ScanProgress>(progress =>
                {
                    StatusMessage = progress.StatusText;
                    ProgressValue = progress.Percentage;
                    IsProgressIndeterminate = progress.IsIndeterminate;
                });

                await _libraryService.ScanFolderForMusicAsync(folderPath, progressReporter);

                _logger.LogInformation("Onboarding scan complete. Navigating to main content");
                await _applicationLifecycle.NavigateToMainContentAsync();
            }
            else
            {
                _logger.LogDebug("User cancelled folder selection during onboarding");
                StatusMessage = InitialWelcomeMessage;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Nagi.WinUI.Resources.Strings.Onboarding_Error;
            _logger.LogCritical(ex, "Critical error during onboarding AddFolder operation");
        }
        finally
        {
            IsAddingFolder = false;
            IsParsing = false;
            ProgressValue = 0;
            IsProgressIndeterminate = false;
        }
    }
}