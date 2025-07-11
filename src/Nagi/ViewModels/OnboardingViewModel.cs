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

    /// <summary>
    /// Service for managing the music library.
    /// </summary>
    private readonly ILibraryService _libraryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="OnboardingViewModel"/> class.
    /// </summary>
    /// <param name="libraryService">The library service to use for scanning and managing music.</param>
    public OnboardingViewModel(ILibraryService libraryService) {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
    }

    /// <summary>
    /// Gets or sets a value indicating whether a folder selection operation is in progress.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsAddingFolder { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a music parsing/scanning operation is in progress.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsParsing { get; set; }

    /// <summary>
    /// Gets or sets the current status message displayed to the user.
    /// </summary>
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = InitialWelcomeMessage;

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

            // Ensure the application window is available for the folder picker to initialize.
            if (App.RootWindow == null) {
                StatusMessage = "Application window not found. Cannot open folder picker.";
                return;
            }

            IntPtr hwnd = WindowNative.GetWindowHandle(App.RootWindow);
            if (hwnd == IntPtr.Zero) {
                StatusMessage = "Application window is not ready. Please try again.";
                return;
            }

            // Initialize the folder picker with the application window handle.
            InitializeWithWindow.Initialize(folderPicker, hwnd);
            folderPicker.FileTypeFilter.Add("*");

            // Show the folder picker and wait for user selection.
            var selectedFolder = await folderPicker.PickSingleFolderAsync();

            if (selectedFolder != null) {
                IsAddingFolder = false;
                IsParsing = true;
                StatusMessage = "Building your library...";

                // Setup a progress reporter to update the UI during the scan.
                var progressReporter = new Progress<ScanProgress>(progress => {
                    if (progress.Percentage >= 100) {
                        StatusMessage = $"Added {progress.NewSongsFound:N0} songs. Welcome!";
                    }
                    else if (progress.NewSongsFound == 0) {
                        StatusMessage = "Scanning your music collection...";
                    }
                    else if (progress.NewSongsFound == 1) {
                        StatusMessage = "Found 1 new song...";
                    }
                    else {
                        StatusMessage = $"Found {progress.NewSongsFound:N0} new songs...";
                    }
                });

                // Start the music library scan with the selected folder.
                await _libraryService.ScanFolderForMusicAsync(selectedFolder.Path, progressReporter);

                IsParsing = false;
                // Navigate to the main application content after a successful scan.
                App.CurrentApp?.CheckAndNavigateToMainContent();
            }
            else {
                // User cancelled the folder picker, reset status.
                StatusMessage = InitialWelcomeMessage;
            }
        }
        catch (Exception ex) {
            StatusMessage = "An unexpected error occurred. Please try again.";
            // Log critical errors for debugging purposes.
            Debug.WriteLine($"[OnboardingViewModel] Critical error during AddFolder: {ex.Message}");
        }
        finally {
            // Ensure all operation flags are reset regardless of success or failure.
            IsAddingFolder = false;
            IsParsing = false;
        }
    }
}