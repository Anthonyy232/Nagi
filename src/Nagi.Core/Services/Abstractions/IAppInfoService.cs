namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Provides information about the application itself.
/// </summary>
public interface IAppInfoService
{
    string GetAppName();
    string GetAppVersion();
}