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
    IEnumerable<(string Path, DateTime LastWriteTimeUtc)> EnumerateFilesWithLastWriteTime(string path, string searchPattern, SearchOption searchOption);
    string[] GetFiles(string path, string searchPattern);
    Task WriteAllBytesAsync(string path, byte[] bytes);
    Task WriteAllTextAsync(string path, string contents);
    Task<string> ReadAllTextAsync(string path);
    Task<byte[]> ReadAllBytesAsync(string path);

    bool FileExists(string path);
    void DeleteFile(string path);
    void CopyFile(string sourceFileName, string destFileName, bool overwrite);
    void MoveFile(string sourceFileName, string destFileName, bool overwrite);
    DateTime GetLastWriteTimeUtc(string path);
    FileInfo GetFileInfo(string path);
    bool IsHiddenOrSystemFile(string path);
    string GetFileNameWithoutExtension(string path);
    string GetFileName(string path);
    string? GetDirectoryName(string path);
    string GetExtension(string path);
    string Combine(params string[] paths);

    /// <summary>
    ///     Canonicalizes a folder/file path. Resolves mapped network drives to their UNC backing
    ///     path when possible, applies Unicode NFC, normalizes separators, uppercases the drive
    ///     letter, and trims trailing separators. Intended to produce a stable identity for a path
    ///     so that semantically identical paths compare equal.
    /// </summary>
    string NormalizePath(string path);

    /// <summary>
    ///     Returns true if the path's root resolves to a network drive (UNC or a mapped network drive).
    /// </summary>
    bool IsNetworkPath(string path);
}