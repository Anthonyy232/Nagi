using System.Threading.Tasks;

namespace Nagi.Presentation.Services.Abstractions;

public interface IApplicationLifecycle
{
    Task NavigateToMainContentAsync();
    Task ResetAndRestartAsync();
    void Restart();
}
