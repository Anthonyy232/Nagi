
namespace Nagi.Core.Services.Data;

public class ArtistMetadataUpdatedEventArgs : EventArgs
{
    public ArtistMetadataUpdatedEventArgs(Guid artistId, string? newLocalImageCachePath)
    {
        ArtistId = artistId;
        NewLocalImageCachePath = newLocalImageCachePath;
    }

    public Guid ArtistId { get; init; }

    public string? NewLocalImageCachePath { get; init; }
}
