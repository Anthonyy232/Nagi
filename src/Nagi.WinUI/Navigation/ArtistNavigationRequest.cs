using Nagi.Core.Models;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Asks to open an artist named on a song, when only the song or the displayed name is known.
/// </summary>
public record ArtistNavigationRequest
{
    public Song? Song { get; init; }
    public string? ArtistName { get; init; }
}
