namespace Nagi.Presentation.Shell;

/// <summary>
///     What a launch asks the app to do besides opening a file, such as a taskbar jump list task.
/// </summary>
/// <remarks>
///     A second instance forwards these by value to the running one, so keep the values stable.
/// </remarks>
public enum LaunchCommand
{
    None = 0,
    ShuffleAll = 1,
    MiniPlayer = 2
}
