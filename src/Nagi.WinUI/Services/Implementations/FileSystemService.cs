using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

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
        return SafeFileEnumerator
            .EnumerateFilesWithLastWriteTime(path, searchPattern, searchOption)
            .Select(entry => entry.Path);
    }

    public IEnumerable<(string Path, DateTime LastWriteTimeUtc)> EnumerateFilesWithLastWriteTime(
        string path, string searchPattern, SearchOption searchOption)
    {
        return SafeFileEnumerator.EnumerateFilesWithLastWriteTime(path, searchPattern, searchOption);
    }

    public string[] GetFiles(string path, string searchPattern)
    {
        return Directory.GetFiles(path, searchPattern);
    }

    public Task WriteAllBytesAsync(string path, byte[] bytes)
    {
        return File.WriteAllBytesAsync(path, bytes);
    }

    public Task WriteAllTextAsync(string path, string contents)
    {
        return File.WriteAllTextAsync(path, contents);
    }

    public Task<string> ReadAllTextAsync(string path)
    {
        return File.ReadAllTextAsync(path);
    }

    public Task<byte[]> ReadAllBytesAsync(string path)
    {
        return File.ReadAllBytesAsync(path);
    }

    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public void DeleteFile(string path)
    {
        File.Delete(path);
    }

    public void CopyFile(string sourceFileName, string destFileName, bool overwrite)
    {
        File.Copy(sourceFileName, destFileName, overwrite);
    }

    public void MoveFile(string sourceFileName, string destFileName, bool overwrite)
    {
        File.Move(sourceFileName, destFileName, overwrite);
    }

    public DateTime GetLastWriteTimeUtc(string path)
    {
        return File.GetLastWriteTimeUtc(path);
    }

    public FileInfo GetFileInfo(string path)
    {
        return new FileInfo(path);
    }

    public bool IsHiddenOrSystemFile(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Attributes.HasFlag(FileAttributes.Hidden) || fileInfo.Attributes.HasFlag(FileAttributes.System);
        }
        catch
        {
            return false;
        }
    }

    public string GetFileNameWithoutExtension(string path)
    {
        return Path.GetFileNameWithoutExtension(path);
    }

    public string GetFileName(string path)
    {
        return Path.GetFileName(path);
    }

    public string? GetDirectoryName(string path)
    {
        return Path.GetDirectoryName(path);
    }

    public string GetExtension(string path)
    {
        return Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
    }

    public string Combine(params string[] paths)
    {
        return Path.Combine(paths);
    }

    public string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        string working = path;

        // Resolve to an absolute path first so relative segments don't survive.
        try { working = Path.GetFullPath(working); }
        catch { /* Invalid path — fall back to canonical text-only normalization */ }

        // If this is a mapped network drive (Z:\...), resolve to its UNC backing so that the
        // same physical location produces a stable identity regardless of drive-letter remaps.
        working = ResolveMappedDriveToUnc(working);

        return PathCanonicalizer.Normalize(working);
    }

    public bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (PathCanonicalizer.IsUncPath(path)) return true;

        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            if (root.StartsWith(@"\\")) return true;
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    // --- Mapped drive -> UNC resolution via WNetGetUniversalNameW ----------------------------

    private const int UNIVERSAL_NAME_INFO_LEVEL = 1;
    private const int ERROR_SUCCESS = 0;
    private const int ERROR_MORE_DATA = 234;

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int WNetGetUniversalNameW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpLocalPath,
        int dwInfoLevel,
        IntPtr lpBuffer,
        ref int lpBufferSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UNIVERSAL_NAME_INFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lpUniversalName;
    }

    private static string ResolveMappedDriveToUnc(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.StartsWith(@"\\")) return path; // already UNC
        if (path.Length < 2 || path[1] != ':') return path; // not drive-rooted

        try
        {
            var driveInfo = new DriveInfo(path.Substring(0, 2));
            if (driveInfo.DriveType != DriveType.Network) return path;
        }
        catch { return path; }

        // First call to learn the required buffer size.
        int bufferSize = 0;
        int result = WNetGetUniversalNameW(path, UNIVERSAL_NAME_INFO_LEVEL, IntPtr.Zero, ref bufferSize);
        if (result != ERROR_MORE_DATA || bufferSize <= 0) return path;

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = WNetGetUniversalNameW(path, UNIVERSAL_NAME_INFO_LEVEL, buffer, ref bufferSize);
            if (result != ERROR_SUCCESS) return path;

            var info = Marshal.PtrToStructure<UNIVERSAL_NAME_INFO>(buffer);
            return string.IsNullOrEmpty(info.lpUniversalName) ? path : info.lpUniversalName;
        }
        catch
        {
            return path;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
