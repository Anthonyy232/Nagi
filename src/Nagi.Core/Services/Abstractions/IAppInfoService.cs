namespace Nagi.Core.Services.Abstractions;

public interface IAppInfoService
{
    string GetAppName();
    string GetAppVersion();
    Task<IReadOnlyList<string>> GetAvailableLanguagesAsync();
    Task InitializeAsync();
}
