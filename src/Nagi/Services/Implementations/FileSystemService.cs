using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nagi.Services.Abstractions;

namespace Nagi.Services.Implementations;

/// <summary>
///     A concrete implementation of IFileSystemService that wraps standard System.IO operations.
///     This allows for abstracting file system interactions, which is crucial for unit testing.
/// </summary>
public class FileSystemService : IFileSystemService
{
    public bool DirectoryExists(string path)
    {
        return Directory.Exists(path);
    }

    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        Directory.Delete(path, recursive);
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.EnumerateFiles(path, searchPattern, searchOption);
    }

    public Task WriteAllBytesAsync(string path, byte[] bytes)
    {
        return File.WriteAllBytesAsync(path, bytes);
    }

    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public void DeleteFile(string path)
    {
        File.Delete(path);
    }

    public DateTime GetLastWriteTimeUtc(string path)
    {
        return Directory.GetLastWriteTimeUtc(path);
    }

    public FileInfo GetFileInfo(string path)
    {
        return new FileInfo(path);
    }

    public string GetFileNameWithoutExtension(string path)
    {
        return Path.GetFileNameWithoutExtension(path);
    }

    public string GetDirectoryNameFromPath(string path)
    {
        return Path.GetFileName(path);
    }

    public string GetExtension(string path)
    {
        return Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
    }

    public string Combine(params string[] paths)
    {
        return Path.Combine(paths);
    }
}