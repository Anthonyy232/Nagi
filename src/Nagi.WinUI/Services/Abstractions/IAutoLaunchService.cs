using System.Threading.Tasks;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
///     Starts the app when the user signs in, through the operating system's startup registration.
/// </summary>
public interface IAutoLaunchService
{
    /// <returns>False when the state can't be read.</returns>
    Task<bool> IsEnabledAsync();

    Task SetEnabledAsync(bool isEnabled);
}
