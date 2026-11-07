namespace Nagi.Presentation.Lyrics;

/// <summary>
///     The lyrics editor, which saves the edited lyrics or removes the saved ones.
/// </summary>
public sealed class LyricsEditorDialogViewModel
{
    private readonly string _savedLyrics;

    public LyricsEditorDialogViewModel(string savedLyrics, bool canRemove)
    {
        _savedLyrics = savedLyrics;
        Lyrics = savedLyrics;
        CanRemove = canRemove;
    }

    public string Lyrics { get; set; }

    public bool CanRemove { get; }

    // Line endings don't count as a change, since a text box may convert them.
    public bool CanSave => !string.IsNullOrWhiteSpace(Lyrics) &&
                           Lyrics.ReplaceLineEndings("\n") != _savedLyrics.ReplaceLineEndings("\n");

    public LyricsEdit Save() => new(LyricsEditAction.Save, Lyrics);
}
