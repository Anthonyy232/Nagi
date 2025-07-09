using System.IO;
using TagLib;

namespace Nagi.Helpers;

/// <summary>
/// A custom TagLib# file abstraction that opens files in read-only mode.
/// This is crucial for reading files from a packaged application's read-only install directory.
/// </summary>
public class NonWritableFileAbstraction : TagLib.File.IFileAbstraction {
    public string Name { get; }
    public Stream ReadStream { get; }
    public Stream WriteStream => throw new System.NotSupportedException("Write access is not supported.");

    public NonWritableFileAbstraction(string path) {
        Name = path;
        // Open the file with read-only access and allow other processes to read it too.
        ReadStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public void CloseStream(Stream stream) {
        stream.Close();
    }
}