using System.Threading.Tasks;

namespace Nagi.Presentation.Services.Abstractions;

public interface IApplicationLifecycle
{
    /// <summary>
    ///     Shows the main shell when a library folder exists, or onboarding when there is none.
    /// </summary>
    Task NavigateToMainContentAsync();

    void Restart();
}
