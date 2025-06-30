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
///     Manages the state and logic for the initial user onboarding process.
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private const string InitialWelcomeMessage = "Let's set up your music library to get started.";
    private readonly ILibraryService _libraryService;

    public OnboardingViewModel(ILibraryService libraryService)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsAddingFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyOperationInProgress))]
    public partial bool IsParsing { get; set; }

    [ObservableProperty] public partial double ParsingProgressValue { get; set; }

    [ObservableProperty] public partial string StatusMessage { get; set; } = InitialWelcomeMessage;

    /// <summary>
    ///     A composite status indicating if any background operation is active.
    /// </summary>
    public bool IsAnyOperationInProgress => IsAddingFolder || IsParsing;

    /// <summary>
    ///     Initiates the folder selection and library scanning process.
    /// </summary>
    [RelayCommand]
    private async Task AddFolder()
    {
        if (IsAnyOperationInProgress) return;

        IsAddingFolder = true;
        StatusMessage = "Waiting for you to select a folder...";

        try
        {
            var folderPicker = new FolderPicker();

            // Defensive check to ensure the window and its handle are ready.
            if (App.RootWindow == null)
            {
                StatusMessage = "Application window not found. Cannot open folder picker.";
                Debug.WriteLine("[OnboardingViewModel] App.RootWindow is null.");
                IsAddingFolder = false;
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(App.RootWindow);
            if (hwnd == IntPtr.Zero)
            {
                StatusMessage = "Application window is not ready. Please try again.";
                Debug.WriteLine("[OnboardingViewModel] Failed to get a valid window handle (HWND).");
                IsAddingFolder = false;
                return;
            }

            InitializeWithWindow.Initialize(folderPicker, hwnd);
            folderPicker.FileTypeFilter.Add("*");

            var selectedFolder = await folderPicker.PickSingleFolderAsync();

            if (selectedFolder != null)
            {
                StatusMessage = $"Adding folder: {selectedFolder.DisplayName}...";
                var addedAppFolder =
                    await _libraryService.AddFolderAsync(selectedFolder.Path, selectedFolder.DisplayName);

                if (addedAppFolder != null)
                {
                    IsAddingFolder = false;
                    IsParsing = true;

                    var progressReporter = new Progress<ScanProgress>(progress =>
                    {
                        ParsingProgressValue = progress.Percentage;
                        StatusMessage = $"Scanning '{addedAppFolder.Name}'";
                    });

                    await Task.Run(() =>
                        _libraryService.ScanFolderForMusicAsync(addedAppFolder.Path, progressReporter));

                    StatusMessage = "Library created successfully!";
                    App.CurrentApp?.CheckAndNavigateToMainContent();
                }
                else
                {
                    StatusMessage = "This folder is already in your library.";
                    IsAddingFolder = false;
                }
            }
            else
            {
                StatusMessage = InitialWelcomeMessage;
                IsAddingFolder = false;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "An unexpected error occurred.";
            Debug.WriteLine($"[OnboardingViewModel] Critical error during AddFolder: {ex.Message}");
            IsAddingFolder = false;
            IsParsing = false;
        }
    }
}