using Microsoft.Extensions.DependencyInjection;
using Nagi.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Nagi.Services.Implementations.WinUI;

public class WinUIApplicationLifecycle : IApplicationLifecycle {
    private readonly App _app;
    private readonly IServiceProvider _serviceProvider;

    public WinUIApplicationLifecycle(App app, IServiceProvider serviceProvider) {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public async Task NavigateToMainContentAsync() {
        await _app.CheckAndNavigateToMainContent();
    }

    public async Task ResetAndNavigateToOnboardingAsync() {
        try {
            var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
            await settingsService.ResetToDefaultsAsync();

            var libraryService = _serviceProvider.GetRequiredService<ILibraryService>();
            await libraryService.ClearAllLibraryDataAsync();

            var playbackService = _serviceProvider.GetRequiredService<IMusicPlaybackService>();
            await playbackService.ClearQueueAsync();

            await _app.CheckAndNavigateToMainContent();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[CRITICAL] Application reset failed. Error: {ex.Message}\n{ex.StackTrace}");
            // Re-throw to allow the ViewModel to handle showing an error dialog.
            throw;
        }
    }
}