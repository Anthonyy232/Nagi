using Nagi.WinUI.Navigation;

namespace Nagi.WinUI.Services.Abstractions;

public interface INavigationService
{
    void NavigateTo(INavigationParameter parameter);
}
