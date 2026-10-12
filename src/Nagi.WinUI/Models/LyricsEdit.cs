namespace Nagi.WinUI.Models;

public enum LyricsEditAction
{
    Save,
    Remove
}

/// <summary>
///     What the user chose in the lyrics editor: save <see cref="Lyrics" /> or remove the saved lyrics.
/// </summary>
public sealed record LyricsEdit(LyricsEditAction Action, string Lyrics = "");
