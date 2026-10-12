using System.Threading.Tasks;

namespace Nagi.WinUI.Services.Abstractions;

public interface ITrayPopupService
{
    void ShowOrHidePopup();

    Task HidePopup();
}
