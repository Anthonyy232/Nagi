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
    string[] GetFiles(string path, string searchPattern);
    Task WriteAllBytesAsync(string path, byte[] bytes);
    Task WriteAllTextAsync(string path, string contents);
    Task<string> ReadAllTextAsync(string path);
    Task<byte[]> ReadAllBytesAsync(string path);

    bool FileExists(string path);
    void DeleteFile(string path);
    void MoveFile(string sourceFileName, string destFileName, bool overwrite);
    DateTime GetLastWriteTimeUtc(string path);
    FileInfo GetFileInfo(string path);
    string GetFileNameWithoutExtension(string path);
    string? GetDirectoryName(string path);
    string GetExtension(string path);
    string Combine(params string[] paths);
}