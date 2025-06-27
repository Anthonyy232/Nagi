namespace Nagi.Services.Data {
    /// <summary>
    /// A clean Data Transfer Object (DTO) representing artist information
    /// fetched from an external service like Last.fm.
    /// </summary>
    public class ArtistInfo {
        public string Biography { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
    }
}