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

public partial class OnboardingViewModel : ObservableObject
{
    private readonly IApplicationLifecycle _applicationLifecycle;

    private readonly ILibraryScanner _libraryScanner;
    private readonly ILogger<OnboardingViewModel> _logger;
    private readonly IUIService _uiService;

    public OnboardingViewModel(ILibraryScanner libraryScanner, IUIService uiService,
        IApplicationLifecycle applicationLifecycle, ILogger<OnboardingViewModel> logger)
    {
        _libraryScanner = libraryScanner ?? throw new ArgumentNullException(nameof(libraryScanner));
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

    [ObservableProperty] public partial string StatusMessage { get; set; } = Strings.Onboarding_Welcome;

    [ObservableProperty] public partial double ProgressValue { get; set; }

    [ObservableProperty] public partial bool IsProgressIndeterminate { get; set; }

    public bool IsAnyOperationInProgress => IsAddingFolder || IsParsing;

    [RelayCommand]
    private async Task AddFolder()
    {
        IsAddingFolder = true;
        StatusMessage = Strings.Onboarding_WaitingForSelection;

        try
        {
            var folderPath = await _uiService.PickSingleFolderAsync();

            if (folderPath != null)
            {
                _logger.LogDebug("User selected folder '{FolderPath}' for onboarding", folderPath);
                IsAddingFolder = false;
                IsParsing = true;
                StatusMessage = Strings.Onboarding_BuildingLibrary;
                IsProgressIndeterminate = true;

                var progressReporter = new Progress<ScanProgress>(progress =>
                {
                    StatusMessage = progress.StatusText;
                    ProgressValue = progress.Percentage;
                    IsProgressIndeterminate = progress.IsIndeterminate;
                });

                await _libraryScanner.ScanFolderForMusicAsync(folderPath, progressReporter);

                _logger.LogInformation("Onboarding scan complete. Navigating to main content");
                await _applicationLifecycle.NavigateToMainContentAsync();
            }
            else
            {
                _logger.LogDebug("User cancelled folder selection during onboarding");
                StatusMessage = Strings.Onboarding_Welcome;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Onboarding_Error;
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
