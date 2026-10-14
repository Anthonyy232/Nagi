namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Watches the library's root folders and refreshes a folder in the background when audio files
///     in it are added, removed, renamed or changed.
/// </summary>
public interface ILibraryFolderWatcher
{
    void Start();
}
