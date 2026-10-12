namespace Nagi.WinUI.Models;

/// <summary>
///     The name and optional cover image chosen in the new playlist dialog.
/// </summary>
public sealed record NewPlaylistRequest(string Name, string? CoverImagePath);
