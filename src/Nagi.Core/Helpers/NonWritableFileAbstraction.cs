using File = TagLib.File;

namespace Nagi.Core.Helpers;

/// <summary>
///     A custom TagLib# file abstraction that opens files in read-only mode.
///     This is crucial for reading files from a packaged application's read-only install directory.
/// </summary>
public class NonWritableFileAbstraction : File.IFileAbstraction
{
    public NonWritableFileAbstraction(string path)
    {
        Name = path;
        // Open the file with read-only access and allow other processes to read it too.
        ReadStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public string Name { get; }
    public Stream ReadStream { get; }
    public Stream WriteStream => throw new NotSupportedException("Write access is not supported.");

    public void CloseStream(Stream stream)
    {
        stream.Close();
    }
}