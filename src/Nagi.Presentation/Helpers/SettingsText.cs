using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Helpers;

/// <summary>
///     The names settings choices are shown with.
/// </summary>
public static class SettingsText
{
    public static string GetName(AppTheme theme) => theme switch
    {
        AppTheme.Light => Strings.Theme_Light,
        AppTheme.Dark => Strings.Theme_Dark,
        _ => Strings.Theme_Default
    };

    public static string GetName(BackdropMaterial material) => material switch
    {
        BackdropMaterial.MicaAlt => Strings.Backdrop_MicaAlt,
        BackdropMaterial.Acrylic => Strings.Backdrop_Acrylic,
        _ => Strings.Backdrop_Mica
    };

    public static string GetName(PlayerBackgroundMaterial material) => material switch
    {
        PlayerBackgroundMaterial.Solid => Strings.PlayerBackgroundMaterial_Solid,
        _ => Strings.PlayerBackgroundMaterial_Acrylic
    };

    /// <param name="pageSize">Songs per page; 0 shows every song on one page.</param>
    public static string GetPageSizeName(int pageSize) =>
        pageSize == 0 ? Strings.PaginationControl_All : pageSize.ToString();
}
