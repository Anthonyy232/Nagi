namespace Nagi.Presentation.Helpers;

/// <summary>
///     When the playlist dialogs accept a name.
/// </summary>
public static class PlaylistNames
{
    public static bool IsValid(string name) => !string.IsNullOrWhiteSpace(name);

    public static bool CanRename(string newName, string currentName) => IsValid(newName) && newName.Trim() != currentName;
}
