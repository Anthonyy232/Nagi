namespace Nagi.Presentation.Models;

public enum LyricsEditAction
{
    Save,
    Remove
}

/// <summary>
///     What the user chose in the lyrics editor: save <see cref="Lyrics" /> or remove the saved lyrics.
/// </summary>
public sealed record LyricsEdit(LyricsEditAction Action, string Lyrics = "")
{
    /// <summary>
    ///     Whether the editor can save <paramref name="edited" />. Line endings don't count as a change, since a text
    ///     box may convert them.
    /// </summary>
    public static bool CanSave(string edited, string original) =>
        !string.IsNullOrWhiteSpace(edited) && edited.ReplaceLineEndings("\n") != original.ReplaceLineEndings("\n");
}
