using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Services.Abstractions;
using Nagi.Services.Implementations;

namespace Nagi.ViewModels;

public partial class OnboardingViewModel : ObservableObject {
    private const string InitialWelcomeMessage = "Let's set up your music library to get started.";

    private readonly ILibraryService _libraryService;
    private readonly IUIService _uiService;
    private readonly IApplicationLifecycle _applicationLifecycle;

    public OnboardingViewModel(ILibraryService libraryService, IUIService uiService, IApplicationLifecycle applicationLifecycle) {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _applicationLifecycle = applicationLifecycle ?? throw new ArgumentNullException(nameof(applicationLifecycle));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    private bool _isAddingFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    private bool _isParsing;

    [ObservableProperty]
    private string _statusMessage = InitialWelcomeMessage;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    public bool IsAnyOperationInProgress => IsAddingFolder || IsParsing;

    [RelayCommand]
    private async Task AddFolder() {
        if (IsAnyOperationInProgress) {
            return;
        }

        IsAddingFolder = true;
        StatusMessage = "Waiting for you to select a folder...";

        try {
            var folderPath = await _uiService.PickSingleFolderAsync();

            if (folderPath != null) {
                IsAddingFolder = false;
                IsParsing = true;
                StatusMessage = "Building your library...";
                IsProgressIndeterminate = true;

                var progressReporter = new Progress<ScanProgress>(progress => {
                    StatusMessage = progress.StatusText;
                    ProgressValue = progress.Percentage;
                    IsProgressIndeterminate = progress.IsIndeterminate;
                });

                await _libraryService.ScanFolderForMusicAsync(folderPath, progressReporter);

                await _applicationLifecycle.NavigateToMainContentAsync();
            }
            else {
                StatusMessage = InitialWelcomeMessage;
            }
        }
        catch (Exception ex) {
            StatusMessage = "An unexpected error occurred. Please try again.";
            Debug.WriteLine($"[OnboardingViewModel] Critical error during AddFolder: {ex.Message}");
        }
        finally {
            IsAddingFolder = false;
            IsParsing = false;
            ProgressValue = 0;
            IsProgressIndeterminate = false;
        }
    }
}