namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Provides an abstraction over the file system to enable testability and platform-specific implementations.
/// </summary>
public interface IFileSystemService
{
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void DeleteDirectory(string path, bool recursive);
    IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption);
    Task WriteAllBytesAsync(string path, byte[] bytes);
    bool FileExists(string path);
    void DeleteFile(string path);
    DateTime GetLastWriteTimeUtc(string path);
    FileInfo GetFileInfo(string path);
    string GetFileNameWithoutExtension(string path);
    string GetDirectoryNameFromPath(string path);
    string GetExtension(string path);
    string Combine(params string[] paths);
}