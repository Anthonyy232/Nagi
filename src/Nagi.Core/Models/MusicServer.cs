using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nagi.Core.Models;

public enum MusicServerKind
{
    OpenSubsonic,
    Jellyfin
}

/// <summary>
///     A media server whose songs are synced into the library under their own root <see cref="Folder" />.
///     The password, API key or access token lives in the credential locker, not in the database.
/// </summary>
public class MusicServer
{
    [Key] public Guid FolderId { get; set; }

    [ForeignKey(nameof(FolderId))] public virtual Folder Folder { get; set; } = null!;

    public MusicServerKind Kind { get; set; }

    /// <summary>
    ///     The server address without a trailing slash, for example <c>https://music.example.com</c>.
    /// </summary>
    [Required][MaxLength(1000)] public string BaseUrl { get; set; } = string.Empty;

    [Required][MaxLength(200)] public string UserName { get; set; } = string.Empty;

    /// <summary>
    ///     Jellyfin's ID for the signed-in user. OpenSubsonic servers identify users by name, so it is null for them.
    /// </summary>
    [MaxLength(100)] public string? UserId { get; set; }

    public bool UsesApiKey { get; set; }
}
