using Nagi.Core.MusicServers;

namespace Nagi.Core.Library;

/// <summary>
///     Filters for the songs and folders that have files on disk. A music server's root folder and songs have paths
///     only as unique keys, so scans, file watching, path clean-up and anything else that opens files skip them.
/// </summary>
public static class LibraryQueries
{
    public static IQueryable<Song> OnDisk(this IQueryable<Song> songs) => songs.Where(s => s.RemoteId == null);

    public static IQueryable<Folder> OnDisk(this IQueryable<Folder> folders) => folders.Where(f => f.MusicServer == null);
}
