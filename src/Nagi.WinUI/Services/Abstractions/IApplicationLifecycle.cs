using System.Threading.Tasks;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
///     Abstracts application-level lifecycle and navigation events.
/// </summary>
public interface IApplicationLifecycle
{
    Task NavigateToMainContentAsync();
    Task ResetAndNavigateToOnboardingAsync();
}