using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Settings;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services;
using Windows.UI.StartScreen;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     The tasks in the taskbar icon's right-click menu. Each relaunches the app with an argument, which a running
///     instance receives through the single-instance pipe and hands to the <see cref="LaunchCoordinator" />.
/// </summary>
public sealed class JumpListService : IDisposable
{
    private const string ShuffleAllArgument = "--shuffle-all";
    private const string MiniPlayerArgument = "--mini-player";

    private readonly ISettingsService _settingsService;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger<JumpListService> _logger;

    public JumpListService(ISettingsService settingsService, DispatcherQueue dispatcherQueue,
        ILogger<JumpListService> logger)
    {
        _settingsService = settingsService;
        _dispatcherQueue = dispatcherQueue;
        _logger = logger;
    }

    public static LaunchCommand Parse(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals(ShuffleAllArgument, StringComparison.OrdinalIgnoreCase)) return LaunchCommand.ShuffleAll;
            if (arg.Equals(MiniPlayerArgument, StringComparison.OrdinalIgnoreCase)) return LaunchCommand.MiniPlayer;
        }

        return LaunchCommand.None;
    }

    /// <summary>
    ///     Writes the jump list once the launch has settled, and again whenever the mini player setting changes.
    /// </summary>
    public void Start()
    {
        _settingsService.SettingChanged += OnSettingChanged;
        _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = RegisterAsync());
    }

    public void Dispose() => _settingsService.SettingChanged -= OnSettingChanged;

    // The mini player only exists while "Minimize to Mini-Player" is on, so its task follows the setting.
    private void OnSettingChanged(Setting setting)
    {
        if (setting == AppSettings.MinimizeToMiniPlayer) _dispatcherQueue.TryEnqueue(() => _ = RegisterAsync());
    }

    // Rewritten on every launch so that the labels follow the current app language.
    private async Task RegisterAsync()
    {
        try
        {
            if (!JumpList.IsSupported()) return;

            var jumpList = await JumpList.LoadCurrentAsync();
            jumpList.SystemGroupKind = JumpListSystemGroupKind.None;
            jumpList.Items.Clear();
            jumpList.Items.Add(CreateTask(ShuffleAllArgument, Strings.JumpList_ShuffleAll, "JumpListShuffle"));
            if (_settingsService.Get(AppSettings.MinimizeToMiniPlayer))
                jumpList.Items.Add(CreateTask(MiniPlayerArgument, Strings.JumpList_MiniPlayer, "JumpListMiniPlayer"));
            await jumpList.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register the taskbar jump list");
        }
    }

    private static JumpListItem CreateTask(string argument, string displayName, string iconName)
    {
        var item = JumpListItem.CreateWithArguments(argument, displayName);
        item.Logo = new Uri($"ms-appx:///Assets/{iconName}.png");
        return item;
    }
}
