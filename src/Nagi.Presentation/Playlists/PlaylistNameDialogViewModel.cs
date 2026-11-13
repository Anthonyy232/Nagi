using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Metadata;
using Nagi.Presentation.Common;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Playlists;

/// <summary>
///     The new playlist and rename playlist dialogs.
/// </summary>
/// <param name="currentName">The name of the playlist being renamed; <c>null</c> for a new playlist.</param>
public sealed partial class PlaylistNameDialogViewModel(IUIService uiService, string? currentName = null) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    public partial string Name { get; set; } = currentName ?? string.Empty;

    /// <summary>The cover image chosen for a new playlist, if any.</summary>
    [ObservableProperty] public partial string? CoverImagePath { get; set; }

    public bool IsNewPlaylist => currentName is null;

    public string Title => currentName is null
        ? Strings.PlaylistPage_CreateDialog_Title
        : ResourceFormatter.Format(Strings.PlaylistPage_RenameDialog_Title_Format, currentName);

    public string AcceptText => currentName is null
        ? Strings.PlaylistPage_CreateDialog_CreateButton
        : Strings.PlaylistPage_RenameDialog_RenameButton;

    public bool CanAccept => !string.IsNullOrWhiteSpace(Name) && (currentName is null || Name.Trim() != currentName);

    [RelayCommand]
    private async Task PickCoverImageAsync()
    {
        var file = await uiService.PickSingleFileAsync(FileExtensions.ImageFileExtensions);
        if (!string.IsNullOrWhiteSpace(file)) CoverImagePath = file;
    }
}
