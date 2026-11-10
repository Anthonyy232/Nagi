using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Presentation.Common;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Lyrics;

/// <summary>
///     The lyrics editor, which saves the edited lyrics or removes the saved ones.
/// </summary>
public sealed partial class LyricsEditorDialogViewModel : ObservableObject
{
    private readonly string _savedLyrics;

    public LyricsEditorDialogViewModel(string savedLyrics, bool canRemove)
    {
        _savedLyrics = savedLyrics;
        Lyrics = savedLyrics;
        RemoveButtonText = canRemove ? Strings.LyricsPage_EditLyrics_Remove : string.Empty;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial string Lyrics { get; set; }

    /// <summary>Empty when there are no saved lyrics to remove, which hides the button.</summary>
    public string RemoveButtonText { get; }

    // Line endings don't count as a change, since a text box may convert them.
    public bool CanSave => !string.IsNullOrWhiteSpace(Lyrics) &&
                           Lyrics.ReplaceLineEndings("\n") != _savedLyrics.ReplaceLineEndings("\n");

    public LyricsEdit Save() => new(LyricsEditAction.Save, Lyrics);
}
