using System.IO;
using Nagi.Core.Helpers;
using Windows.Storage;

namespace Nagi.WinUI.Helpers;

internal static class PackagedAppData
{
    /// <summary>
    ///     The package's local folder, or <c>%LOCALAPPDATA%\Nagi</c> when the app runs unpackaged (such as in tests).
    /// </summary>
    internal static string LocalFolderPath
    {
        get
        {
            try
            {
                return ApplicationData.Current.LocalFolder.Path;
            }
            catch (Exception)
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nagi");
            }
        }
    }

    internal static PathConfiguration CreatePathConfiguration() => new(LocalFolderPath);
}
