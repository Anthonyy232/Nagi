using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Nagi.WinUI.Helpers;

public static class BitmapImageFactory
{
    /// <returns>Null if <paramref name="uriString" /> is not an absolute URI.</returns>
    public static ImageSource? Create(string? uriString)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            return null;

        try
        {
            return new BitmapImage(uri);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Creates a BitmapImage with an explicit logical decode width. The decode hint must
    ///     be set BEFORE the URI source so the framework decodes at the requested resolution
    ///     instead of the source's native size — using new BitmapImage(uri) starts decoding
    ///     immediately and ignores any subsequent DecodePixelWidth assignment.
    ///     The framework multiplies the logical width by the active DPI scale.
    /// </summary>
    public static ImageSource? CreateDecoded(string? uriString, int decodePixelWidth)
    {
        if (decodePixelWidth <= 0 || !Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            return null;

        try
        {
            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = decodePixelWidth,
            };
            bitmap.UriSource = uri;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
