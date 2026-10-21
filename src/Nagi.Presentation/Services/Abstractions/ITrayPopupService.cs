namespace Nagi.Presentation.Services.Abstractions;

/// <summary>
///     The small player that opens from the tray (or menu bar) icon.
/// </summary>
public interface ITrayPopupService
{
    void TogglePopup();

    void HidePopup();
}
