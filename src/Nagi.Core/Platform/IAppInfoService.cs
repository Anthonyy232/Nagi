namespace Nagi.Core.Platform;

public interface IAppInfoService
{
    string GetAppName();
    string GetAppVersion();
    Task<IReadOnlyList<string>> GetAvailableLanguagesAsync();
}
