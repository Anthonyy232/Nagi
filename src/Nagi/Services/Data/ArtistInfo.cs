namespace Nagi.Services.Data {
    /// <summary>
    /// Represents consolidated artist information, including a biography and an image URL.
    /// This object is the final result returned by the music information service.
    /// </summary>
    public class ArtistInfo {
        /// <summary>
        /// Gets or sets the artist's biography.
        /// </summary>
        public string Biography { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the URL for an image of the artist. This can be null if no image is found.
        /// </summary>
        public string? ImageUrl { get; set; }
    }
}