namespace Nagi.Core.Platform;

public interface IAppInfoService
{
    string GetAppName();
    string GetAppVersion();

    /// <summary>Installed from the Microsoft Store, not from a GitHub release.</summary>
    bool IsStoreInstall { get; }

    Task<IReadOnlyList<string>> GetAvailableLanguagesAsync();
}
