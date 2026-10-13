using System.Threading.Tasks;

namespace Nagi.WinUI.Services.Abstractions;

public interface IApplicationLifecycle
{
    Task NavigateToMainContentAsync();
    Task ResetAndRestartAsync();
    void Restart();
}
