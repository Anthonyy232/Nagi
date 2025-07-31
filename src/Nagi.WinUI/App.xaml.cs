using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Services.Implementations.Presence;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;
using Nagi.WinUI.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.UI;
using WinRT;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

#if !MSIX_PACKAGE
using Velopack;
using Velopack.Sources;
#endif

namespace Nagi.WinUI {
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application {
        private static Color? _systemAccentColor;
        private Window? _window;
        private MicaController? _micaController;
        private WindowsSystemDispatcherQueueHelper? _wsdqHelper;

        private readonly ConcurrentQueue<string> _fileActivationQueue = new();
        private volatile bool _isProcessingFileQueue = false;

        /// <summary>
        /// Initializes the singleton application object.
        /// </summary>
        public App() {
            Debug.WriteLine("App.Constructor: Initializing application.");

            CurrentApp = this;
            InitializeComponent();
            UnhandledException += OnAppUnhandledException;
            CoreApplication.Suspending += OnSuspending;
            LibVLCSharp.Core.Initialize();
        }

        /// <summary>
        /// Gets the current application instance.
        /// </summary>
        public static App? CurrentApp { get; private set; }

        /// <summary>
        /// Gets the main application window.
        /// </summary>
        public static Window? RootWindow => CurrentApp?._window;

        /// <summary>
        /// Gets the service provider for dependency injection.
        /// </summary>
        public static IServiceProvider? Services { get; private set; }

        /// <summary>
        /// Gets the dispatcher queue for the main UI thread.
        /// </summary>
        public static DispatcherQueue? MainDispatcherQueue => CurrentApp?._window?.DispatcherQueue;

        /// <summary>
        /// Gets the system's accent color.
        /// </summary>
        public static Color SystemAccentColor {
            get {
                if (_systemAccentColor is null) {
                    if (Current.Resources.TryGetValue("SystemAccentColor", out object? value) && value is Color color) {
                        _systemAccentColor = color;
                    }
                    else {
                        _systemAccentColor = Colors.SlateGray;
                    }
                }
                return _systemAccentColor.Value;
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args) {
            Debug.WriteLine("App.OnLaunched: Launch sequence started.");

            // Stage 1: Initialize the main window and dependency injection services.
            Debug.WriteLine("App.OnLaunched: Stage 1 - Initializing Window and Services.");
            InitializeWindowAndServices();
            InitializeSystemIntegration();
            Debug.WriteLine("App.OnLaunched: Stage 1 - Complete.");

            // Stage 2: Handle initial activation arguments (e.g., opening a file on launch).
            Debug.WriteLine("App.OnLaunched: Stage 2 - Handling launch arguments.");
            HandleInitialActivation(args.UWPLaunchActivatedEventArgs);
            Debug.WriteLine("App.OnLaunched: Stage 2 - Complete.");

            // Stage 3: Initialize core application services.
            // If a file was passed on launch, do not restore the previous session.
            bool restoreSession = _fileActivationQueue.IsEmpty;
            Debug.WriteLine($"App.OnLaunched: Stage 3 - Initializing Core Services. Restore Session = {restoreSession}");
            await InitializeCoreServicesAsync(restoreSession);
            Debug.WriteLine("App.OnLaunched: Stage 3 - Complete.");

            // Stage 4: Navigate to the appropriate initial page (Onboarding or Main).
            Debug.WriteLine("App.OnLaunched: Stage 4 - Navigating to main content.");
            await CheckAndNavigateToMainContent();
            Debug.WriteLine("App.OnLaunched: Stage 4 - Complete.");

            // Stage 5: Process any file activations that were queued.
            Debug.WriteLine("App.OnLaunched: Stage 5 - Processing activation queue.");
            ProcessFileActivationQueue();
            Debug.WriteLine("App.OnLaunched: Stage 5 - Complete.");

            // Stage 6: Activate the window and perform post-launch tasks.
            Debug.WriteLine("App.OnLaunched: Stage 6 - Handling window activation and post-launch tasks.");
            bool isStartupLaunch = Environment.GetCommandLineArgs().Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            await HandleWindowActivationAsync(isStartupLaunch);
            PerformPostLaunchTasks();
            Debug.WriteLine("App.OnLaunched: Stage 6 - Complete. Launch sequence finished.");
        }

        /// <summary>
        /// Handles activation arguments from the initial launch.
        /// </summary>
        private void HandleInitialActivation(IActivatedEventArgs args) {
            string? filePath = null;

            // For packaged apps, use the standard file activation event args.
            if (args.Kind == ActivationKind.File) {
                var fileArgs = args.As<IFileActivatedEventArgs>();
                if (fileArgs.Files.Any()) {
                    filePath = fileArgs.Files[0].Path;
                    Debug.WriteLine($"App.HandleInitialActivation: Found file via File Activation: {filePath}");
                }
            }
            // For unpackaged apps, the file path is passed as a command-line argument.
            else if (args.Kind == ActivationKind.Launch) {
                string[] commandLineArgs = Environment.GetCommandLineArgs();
                if (commandLineArgs.Length > 1) {
                    // The first argument is the executable path, the second is the file path.
                    filePath = commandLineArgs[1];
                    Debug.WriteLine($"App.HandleInitialActivation: Found file via Environment.GetCommandLineArgs: {filePath}");
                }
            }

            if (!string.IsNullOrEmpty(filePath)) {
                _fileActivationQueue.Enqueue(filePath);
            }
        }

        /// <summary>
        /// Enqueues a file path for activation and triggers processing.
        /// </summary>
        public void EnqueueFileActivation(string filePath) {
            Debug.WriteLine($"App.EnqueueFileActivation: Received request to enqueue file: {filePath}");
            if (string.IsNullOrEmpty(filePath)) {
                Debug.WriteLine("[WARNING] App.EnqueueFileActivation: File path was null or empty. Ignoring.");
                return;
            }
            _fileActivationQueue.Enqueue(filePath);
            ProcessFileActivationQueue();
        }

        /// <summary>
        /// Processes the queue of file activations on the UI thread.
        /// </summary>
        private void ProcessFileActivationQueue() {
            if (_isProcessingFileQueue) {
                Debug.WriteLine("App.ProcessFileActivationQueue: Already processing, will be handled by existing loop.");
                return;
            }

            MainDispatcherQueue?.TryEnqueue(async () => {
                if (_isProcessingFileQueue) return;

                _isProcessingFileQueue = true;
                Debug.WriteLine("App.ProcessFileActivationQueue: Starting to process queue on UI thread.");
                try {
                    while (_fileActivationQueue.TryDequeue(out var filePath)) {
                        Debug.WriteLine($"App.ProcessFileActivationQueue: Dequeued and processing file: {filePath}");
                        await ProcessFileActivationAsync(filePath);
                    }
                }
                catch (Exception ex) {
                    Debug.WriteLine($"[ERROR] App.ProcessFileActivationQueue: Exception while processing queue: {ex}");
                }
                finally {
                    Debug.WriteLine("App.ProcessFileActivationQueue: Queue is empty. Finished processing.");
                    _isProcessingFileQueue = false;
                }
            });
        }

        /// <summary>
        /// Plays a single audio file without adding it to the library.
        /// </summary>
        public async Task ProcessFileActivationAsync(string filePath) {
            Debug.WriteLine($"App.ProcessFileActivationAsync: Processing transient file: {filePath}");
            if (Services is null || string.IsNullOrEmpty(filePath)) {
                Debug.WriteLine($"[ERROR] App.ProcessFileActivationAsync: Aborting. Services is null? {Services is null}. File path is null/empty? {string.IsNullOrEmpty(filePath)}");
                return;
            }

            Debug.WriteLine($"[App] Processing transient file activation for: {filePath}");

            var playbackService = Services.GetRequiredService<IMusicPlaybackService>();

            _window?.Activate();
            Debug.WriteLine("App.ProcessFileActivationAsync: Window activated.");

            await playbackService.PlayTransientFileAsync(filePath);
            Debug.WriteLine("App.ProcessFileActivationAsync: Call to PlayTransientFileAsync completed.");
        }

        private async Task InitializeCoreServicesAsync(bool restoreSession = true) {
            if (Services is null) return;
            try {
                Debug.WriteLine($"App.InitializeCoreServicesAsync: Starting initialization. Restore Session = {restoreSession}");
                await Services.GetRequiredService<IMusicPlaybackService>().InitializeAsync(restoreSession);
                await Services.GetRequiredService<IPresenceManager>().InitializeAsync();
                await Services.GetRequiredService<TrayIconViewModel>().InitializeAsync();

                var offlineScrobbleService = Services.GetRequiredService<IOfflineScrobbleService>();
                offlineScrobbleService.Start();
                await offlineScrobbleService.ProcessQueueAsync();
                Debug.WriteLine("App.InitializeCoreServicesAsync: Initialization complete.");
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ERROR] App: Failed to initialize services. {ex}");
            }
        }

        private static IServiceProvider ConfigureServices(Window window, DispatcherQueue dispatcherQueue, App appInstance) {
            var services = new ServiceCollection();

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IPathConfiguration, PathConfiguration>();
            services.AddHttpClient();

            ConfigureAppSettingsServices(services);
            ConfigureCoreLogicServices(services);
            ConfigureWinUIServices(services, window, dispatcherQueue, appInstance);
            ConfigureViewModels(services);

            return services.BuildServiceProvider();
        }

        private static void ConfigureAppSettingsServices(IServiceCollection services) {
            services.AddSingleton<ICredentialLockerService, CredentialLockerService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<IUISettingsService>(sp => sp.GetRequiredService<SettingsService>());
            services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        }

        private static void ConfigureCoreLogicServices(IServiceCollection services) {
            services.AddDbContextFactory<MusicDbContext>((serviceProvider, options) => {
                var pathConfig = serviceProvider.GetRequiredService<IPathConfiguration>();
                options.UseSqlite($"Data Source={pathConfig.DatabasePath}");
            });

            services.AddSingleton<LibraryService>();
            services.AddSingleton<ILibraryService>(provider => provider.GetRequiredService<LibraryService>());
            services.AddSingleton<ILibraryReader>(provider => provider.GetRequiredService<LibraryService>());
            services.AddSingleton<ILibraryWriter>(provider => provider.GetRequiredService<LibraryService>());
            services.AddSingleton<ILibraryScanner>(provider => provider.GetRequiredService<LibraryService>());
            services.AddSingleton<IPlaylistService>(provider => provider.GetRequiredService<LibraryService>());

            services.AddSingleton<IMusicPlaybackService, MusicPlaybackService>();
            services.AddSingleton<IOfflineScrobbleService, OfflineScrobbleService>();
            services.AddSingleton<IFileSystemService, FileSystemService>();
            services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
            services.AddSingleton<IMetadataExtractor, TagLibMetadataExtractor>();
            services.AddSingleton<ILrcService, LrcService>();

            services.AddSingleton<IApiKeyService, ApiKeyService>();
            services.AddSingleton<ILastFmMetadataService, LastFmMetadataService>();
            services.AddSingleton<ILastFmAuthService, LastFmAuthService>();
            services.AddSingleton<ISpotifyService, SpotifyService>();
            services.AddSingleton<ILastFmScrobblerService, LastFmScrobblerService>();
            services.AddSingleton<IPresenceManager, PresenceManager>();
            services.AddSingleton<IPresenceService, DiscordPresenceService>();
            services.AddSingleton<IPresenceService, LastFmPresenceService>();
            services.AddSingleton<IUpdateService, VelopackUpdateService>();

#if !MSIX_PACKAGE
            services.AddSingleton(provider =>
            {
                var source = new GithubSource("https://github.com/Anthonyy232/Nagi", null, false);
                return new UpdateManager(source);
            });
#endif
        }

        private static void ConfigureWinUIServices(IServiceCollection services, Window window, DispatcherQueue dispatcherQueue, App appInstance) {
            services.AddSingleton<IWin32InteropService, Win32InteropService>();
            services.AddSingleton<IWindowService>(sp => new WindowService(window, sp.GetRequiredService<IWin32InteropService>()));
            services.AddSingleton<IUIService>(sp => new UIService(window));
            services.AddSingleton(dispatcherQueue);
            services.AddSingleton<IDispatcherService, DispatcherService>();
            services.AddSingleton<IThemeService>(sp => new ThemeService(appInstance, sp));
            services.AddSingleton<IApplicationLifecycle>(sp => new ApplicationLifecycle(appInstance, sp));
            services.AddSingleton<IAppInfoService, AppInfoService>();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<ITrayPopupService, TrayPopupService>();
            services.AddSingleton<IAudioPlayer>(provider => new LibVlcAudioPlayerService(provider.GetRequiredService<IDispatcherService>()));
        }

        private static void ConfigureViewModels(IServiceCollection services) {
            services.AddSingleton<PlayerViewModel>();
            services.AddSingleton<TrayIconViewModel>();

            services.AddTransient<LibraryViewModel>();
            services.AddTransient<PlaylistViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<OnboardingViewModel>();
            services.AddTransient<PlaylistSongListViewModel>();
            services.AddTransient<FolderViewModel>();
            services.AddTransient<FolderSongListViewModel>();
            services.AddTransient<ArtistViewModel>();
            services.AddTransient<ArtistViewViewModel>();
            services.AddTransient<AlbumViewViewModel>();
            services.AddTransient<AlbumViewModel>();
            services.AddTransient<GenreViewModel>();
            services.AddTransient<GenreViewViewModel>();
            services.AddTransient<LyricsPageViewModel>();
        }

        private static void InitializeDatabase(IServiceProvider services) {
            try {
                var dbContextFactory = services.GetRequiredService<IDbContextFactory<MusicDbContext>>();
                using var dbContext = dbContextFactory.CreateDbContext();
                dbContext.Database.Migrate();
            }
            catch (Exception ex) {
                Debug.WriteLine($"[CRITICAL] App: Failed to initialize or migrate database. {ex.Message}");
            }
        }

        private void InitializeWindowAndServices() {
            _window = new MainWindow();
            _window.Closed += OnWindowClosed;
            Services = ConfigureServices(_window, _window.DispatcherQueue, this);
            InitializeDatabase(Services);
        }

        private void InitializeSystemIntegration() {
            try {
                var interopService = Services!.GetRequiredService<IWin32InteropService>();
                interopService.SetWindowIcon(_window!, "Assets/AppLogo.ico");
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ERROR] App: Failed to set window icon. {ex.Message}");
            }

            TrySetMicaBackdrop();
            Services!.GetRequiredService<IThemeService>().ReapplyCurrentDynamicTheme();
        }

        private void PerformPostLaunchTasks() {
            _ = CheckForUpdatesOnStartupAsync();
            EnqueuePostLaunchTasks();
        }

        private async void OnSuspending(object? sender, SuspendingEventArgs e) {
            var deferral = e.SuspendingOperation.GetDeferral();
            if (Services is not null) {
                await SaveApplicationStateAsync(Services);
            }
            deferral.Complete();
        }

        private async Task SaveApplicationStateAsync(IServiceProvider services) {
            var settingsService = services.GetRequiredService<ISettingsService>();
            var musicPlaybackService = services.GetRequiredService<IMusicPlaybackService>();

            try {
                if (await settingsService.GetRestorePlaybackStateEnabledAsync()) {
                    await musicPlaybackService.SavePlaybackStateAsync();
                }
                else {
                    await settingsService.ClearPlaybackStateAsync();
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ERROR] App: Failed to save or clear playback state. {ex.Message}");
            }
        }

        private async void OnWindowClosed(object sender, WindowEventArgs args) {
            if (Services is not null) {
                try {
                    await Services.GetRequiredService<IPresenceManager>().ShutdownAsync();
                    await SaveApplicationStateAsync(Services);
                }
                finally {
                    if (Services is IAsyncDisposable asyncDisposableServices) {
                        await asyncDisposableServices.DisposeAsync();
                    }
                    else if (Services is IDisposable disposableServices) {
                        disposableServices.Dispose();
                    }
                }
            }

            _micaController?.Dispose();
            _micaController = null;
            _wsdqHelper?.Dispose();
            _wsdqHelper = null;

            Application.Current.Exit();
        }

        private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e) {
            Debug.WriteLine($"[FATAL] App: UNHANDLED EXCEPTION: {e.Exception}");
            e.Handled = true;
        }

        /// <summary>
        /// Checks if the library is configured and navigates to the appropriate page.
        /// </summary>
        public async Task CheckAndNavigateToMainContent() {
            if (RootWindow is null || Services is null) return;

            var libraryService = Services.GetRequiredService<ILibraryService>();
            bool hasFolders = (await libraryService.GetAllFoldersAsync()).Any();

            if (hasFolders) {
                if (RootWindow.Content is not MainPage) RootWindow.Content = new MainPage();
                await Services.GetRequiredService<LibraryViewModel>().InitializeAsync();
            }
            else {
                if (RootWindow.Content is not OnboardingPage) RootWindow.Content = new OnboardingPage();
            }

            if (RootWindow.Content is FrameworkElement currentContent && RootWindow is MainWindow mainWindow) {
                var viewSettings = Services.GetRequiredService<IUISettingsService>();
                currentContent.RequestedTheme = await viewSettings.GetThemeAsync();
                mainWindow.InitializeCustomTitleBar();
            }
        }

        internal void ApplyThemeInternal(ElementTheme themeToApply) {
            if (RootWindow?.Content is not FrameworkElement rootElement) return;

            rootElement.RequestedTheme = themeToApply;
            Services?.GetRequiredService<IThemeService>().ReapplyCurrentDynamicTheme();

            if (RootWindow is MainWindow mainWindow) {
                mainWindow.InitializeCustomTitleBar();
            }
        }

        /// <summary>
        /// Sets the color of the application's primary color brush resource.
        /// </summary>
        public void SetAppPrimaryColorBrushColor(Color newColor) {
            if (Resources.TryGetValue("AppPrimaryColorBrush", out object? brushObject) &&
                brushObject is SolidColorBrush appPrimaryColorBrush) {
                if (appPrimaryColorBrush.Color != newColor) {
                    appPrimaryColorBrush.Color = newColor;
                }
            }
            else {
                Debug.WriteLine("[CRITICAL] App: AppPrimaryColorBrush resource not found.");
            }
        }

        /// <summary>
        /// Tries to parse a hex string into a Color object.
        /// </summary>
        public bool TryParseHexColor(string hex, out Color color) {
            color = Colors.Transparent;
            if (string.IsNullOrEmpty(hex)) return false;

            hex = hex.TrimStart('#');
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb)) return false;

            switch (hex.Length) {
                case 6: // RRGGBB
                    color = Color.FromArgb(255, (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                    return true;
                case 8: // AARRGGBB
                    color = Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
                    return true;
                default:
                    return false;
            }
        }

        private async Task HandleWindowActivationAsync(bool isStartupLaunch = false) {
            if (_window is null || Services is null) return;

            var settingsService = Services.GetRequiredService<IUISettingsService>();
            bool startMinimized = await settingsService.GetStartMinimizedEnabledAsync();
            bool hideToTray = await settingsService.GetHideToTrayEnabledAsync();

            if (isStartupLaunch || startMinimized) {
                if (!hideToTray) {
                    WindowActivator.ShowMinimized(_window);
                }
            }
            else {
                _window.Activate();
            }
        }

        private void EnqueuePostLaunchTasks() {
            MainDispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Normal, () => {
                if (Services is null) return;
                try {
                    var audioPlayerService = Services.GetRequiredService<IAudioPlayer>();
                    audioPlayerService.InitializeSmtc();
                }
                catch (Exception ex) {
                    Debug.WriteLine($"[ERROR] App: Failed to initialize System Media Transport Controls. {ex.Message}");
                }
            });
        }

        private bool TrySetMicaBackdrop() {
            if (!MicaController.IsSupported()) return false;

            _wsdqHelper = new WindowsSystemDispatcherQueueHelper();
            _wsdqHelper.EnsureDispatcherQueue();

            var configurationSource = new SystemBackdropConfiguration { IsInputActive = true };

            if (RootWindow?.Content is FrameworkElement rootElement) {
                void UpdateTheme() {
                    configurationSource.Theme = rootElement.ActualTheme switch {
                        ElementTheme.Dark => SystemBackdropTheme.Dark,
                        ElementTheme.Light => SystemBackdropTheme.Light,
                        _ => SystemBackdropTheme.Default
                    };
                }
                rootElement.ActualThemeChanged += (s, e) => UpdateTheme();
                UpdateTheme();
            }

            _micaController = new MicaController();
            _micaController.SetSystemBackdropConfiguration(configurationSource);

            if (RootWindow is not null) {
                _micaController.AddSystemBackdropTarget(RootWindow.As<ICompositionSupportsSystemBackdrop>());
                return true;
            }

            return false;
        }

        private async Task CheckForUpdatesOnStartupAsync() {
            if (Services is null) return;
            try {
#if !MSIX_PACKAGE
                var updateService = Services.GetRequiredService<IUpdateService>();
                await updateService.CheckForUpdatesOnStartupAsync();
#endif
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ERROR] App: Failed during startup update check. {ex.Message}");
            }
        }
    }
}