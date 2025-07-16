namespace Nagi.Services.Abstractions;

/// <summary>
/// Provides information about the application itself.
/// </summary>
public interface IAppInfoService {
    string GetAppName();
    string GetAppVersion();
}