namespace Nagi.WinUI.Styles;

/// <summary>
///     The keys of the app's own XAML resources that code reads or writes, so a renamed resource is renamed here too
///     rather than failing silently at runtime.
/// </summary>
internal static class AppResourceKeys
{
    public const string AppPrimaryColorBrush = nameof(AppPrimaryColorBrush);
    public const string MediaOnImageAccentBrush = nameof(MediaOnImageAccentBrush);
    public const string PlayerTintColorBrush = nameof(PlayerTintColorBrush);
    public const string PlayerBackgroundBrush = nameof(PlayerBackgroundBrush);
    public const string PlayerAccentBrush = nameof(PlayerAccentBrush);
    public const string Win11AcrylicBrush = nameof(Win11AcrylicBrush);
    public const string Win10AcrylicBrush = nameof(Win10AcrylicBrush);

    public const string SongListHyperlinkButtonStyle = nameof(SongListHyperlinkButtonStyle);
    public const string SongListViewPageSecondaryTextBlockStyle = nameof(SongListViewPageSecondaryTextBlockStyle);
    public const string SongListViewPageIconButton = nameof(SongListViewPageIconButton);
    public const string BooleanToVisibilityConverter = nameof(BooleanToVisibilityConverter);

    /// <summary>The path of the dictionary whose default theme resources are the accent overrides.</summary>
    public const string ColorsDictionaryPath = "/Styles/Colors.xaml";
}
