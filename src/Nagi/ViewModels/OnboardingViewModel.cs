using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Services.Abstractions;
using Nagi.Services.Implementations;
using WinRT.Interop;

namespace Nagi.ViewModels;

/// <summary>
/// Manages the state and logic for the initial user onboarding process,
/// including music folder selection and library scanning.
/// </summary>
public partial class OnboardingViewModel : ObservableObject {
    /// <summary>
    /// The initial welcome message displayed to the user.
    /// </summary>
    private const string InitialWelcomeMessage = "Let's set up your music library to get started.";

    private readonly ILibraryService _libraryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="OnboardingViewModel"/> class.
    /// </summary>
    /// <param name="libraryService">The library service to use for scanning and managing music.</param>
    public OnboardingViewModel(ILibraryService libraryService) {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
    }

    /// <summary>
    /// Gets or sets a value indicating whether the application is waiting for the user
    /// to select a folder.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    private bool _isAddingFolder;

    /// <summary>
    /// Gets or sets a value indicating whether a music parsing or scanning operation is in progress.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    private bool _isParsing;

    /// <summary>
    /// Gets or sets the current status message displayed to the user.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = InitialWelcomeMessage;

    /// <summary>
    /// Gets or sets the progress value (0-100) for the current scanning operation.
    /// </summary>
    [ObservableProperty]
    private double _progressValue;

    /// <summary>
    /// Gets or sets a value indicating whether the current progress is indeterminate.
    /// </summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate;

    /// <summary>
    /// Gets a value indicating whether any background operation (folder selection or parsing) is currently active.
    /// This property drives the UI's visual state.
    /// </summary>
    public bool IsAnyOperationInProgress => IsAddingFolder || IsParsing;

    /// <summary>
    /// Command to initiate the folder selection and music library scanning process.
    /// </summary>
    [RelayCommand]
    private async Task AddFolder() {
        // Prevent multiple simultaneous operations.
        if (IsAnyOperationInProgress) {
            return;
        }

        IsAddingFolder = true;
        StatusMessage = "Waiting for you to select a folder...";

        try {
            var folderPicker = new FolderPicker();

            // The window handle is required for the folder picker to be parented correctly.
            if (App.RootWindow == null) {
                StatusMessage = "Application window not found. Cannot open folder picker.";
                // Critical logging for a state that should not happen in a running app.
                Debug.WriteLine("[OnboardingViewModel] App.RootWindow is null. Cannot show FolderPicker.");
                return;
            }

            IntPtr hwnd = WindowNative.GetWindowHandle(App.RootWindow);
            InitializeWithWindow.Initialize(folderPicker, hwnd);
            folderPicker.FileTypeFilter.Add("*");

            var selectedFolder = await folderPicker.PickSingleFolderAsync();

            if (selectedFolder != null) {
                IsAddingFolder = false;
                IsParsing = true;
                StatusMessage = "Building your library...";
                IsProgressIndeterminate = true;

                var progressReporter = new Progress<ScanProgress>(progress => {
                    StatusMessage = progress.StatusText;
                    ProgressValue = progress.Percentage;
                    IsProgressIndeterminate = progress.IsIndeterminate;
                });

                await _libraryService.ScanFolderForMusicAsync(selectedFolder.Path, progressReporter);

                // After the first successful scan, navigate to the main application content.
                App.CurrentApp?.CheckAndNavigateToMainContent();
            }
            else {
                // User cancelled the folder picker.
                StatusMessage = InitialWelcomeMessage;
            }
        }
        catch (Exception ex) {
            StatusMessage = "An unexpected error occurred. Please try again.";
            Debug.WriteLine($"[OnboardingViewModel] Critical error during AddFolder: {ex.Message}");
        }
        finally {
            // Ensure all operation flags are reset regardless of outcome.
            IsAddingFolder = false;
            IsParsing = false;
            ProgressValue = 0;
            IsProgressIndeterminate = false;
        }
    }
}