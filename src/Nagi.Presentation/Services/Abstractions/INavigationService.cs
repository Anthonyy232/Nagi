using Nagi.Presentation.Navigation;

namespace Nagi.Presentation.Services.Abstractions;

public interface INavigationService
{
    void NavigateTo(INavigationParameter parameter);
}
