namespace Nagi.Presentation.Playlists;

/// <summary>
///     The new playlist and rename playlist dialogs.
/// </summary>
/// <param name="currentName">The name of the playlist being renamed; <c>null</c> for a new playlist.</param>
public sealed class PlaylistNameDialogViewModel(string? currentName = null)
{
    public string Name { get; set; } = currentName ?? string.Empty;

    /// <summary>The cover image chosen for a new playlist, if any.</summary>
    public string? CoverImagePath { get; set; }

    public bool CanAccept => !string.IsNullOrWhiteSpace(Name) && (currentName is null || Name.Trim() != currentName);
}
