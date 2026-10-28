using System.Collections.Generic;
using System.Threading.Tasks;
using Nagi.Presentation.Resources;
using Windows.UI.StartScreen;

namespace Nagi.WinUI.Helpers;

internal enum JumpListTask
{
    None,
    ShuffleAll,
    MiniPlayer
}

/// <summary>
///     The tasks in the taskbar icon's right-click menu. Each relaunches the app with an argument,
///     which a running instance receives through the single-instance pipe.
/// </summary>
internal static class JumpListTasks
{
    private const string ShuffleAllArgument = "--shuffle-all";
    private const string MiniPlayerArgument = "--mini-player";

    public static JumpListTask Parse(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals(ShuffleAllArgument, StringComparison.OrdinalIgnoreCase)) return JumpListTask.ShuffleAll;
            if (arg.Equals(MiniPlayerArgument, StringComparison.OrdinalIgnoreCase)) return JumpListTask.MiniPlayer;
        }

        return JumpListTask.None;
    }

    /// <summary>
    ///     Rewrites the jump list so its labels follow the current app language.
    /// </summary>
    public static async Task RegisterAsync(bool includeMiniPlayer)
    {
        if (!JumpList.IsSupported()) return;

        var jumpList = await JumpList.LoadCurrentAsync();
        jumpList.SystemGroupKind = JumpListSystemGroupKind.None;
        jumpList.Items.Clear();
        jumpList.Items.Add(CreateTask(ShuffleAllArgument, Strings.JumpList_ShuffleAll, "JumpListShuffle"));
        if (includeMiniPlayer)
            jumpList.Items.Add(CreateTask(MiniPlayerArgument, Strings.JumpList_MiniPlayer, "JumpListMiniPlayer"));
        await jumpList.SaveAsync();
    }

    private static JumpListItem CreateTask(string argument, string displayName, string iconName)
    {
        var item = JumpListItem.CreateWithArguments(argument, displayName);
        item.Logo = new Uri($"ms-appx:///Assets/{iconName}.png");
        return item;
    }
}
