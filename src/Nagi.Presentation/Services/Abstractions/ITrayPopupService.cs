using System.Threading.Tasks;

namespace Nagi.Presentation.Services.Abstractions;

public interface ITrayPopupService
{
    void ShowOrHidePopup();

    Task HidePopup();
}
