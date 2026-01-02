using Microsoft.UI.Xaml;
using Nagi.Core.Models;
using Nagi.WinUI.Models;
using Windows.UI;

namespace Nagi.WinUI.Services;

/// <summary>
///     Provides centralized default values for application settings to ensure consistency 
///     between the SettingsService and SettingsViewModels.
/// </summary>
public static class SettingsDefaults
{
    public const bool AutoLaunchEnabled = false;
    public const bool PlayerAnimationEnabled = true;
    public const bool ShowQueueButtonEnabled = true;
    public const bool HideToTrayEnabled = true;
    public const bool MinimizeToMiniPlayerEnabled = false;
    public const bool ShowCoverArtInTrayFlyoutEnabled = true;
    public const bool FetchOnlineMetadataEnabled = false;
    public const bool FetchOnlineLyricsEnabled = false;
    public const bool DiscordRichPresenceEnabled = false;
    public const ElementTheme Theme = ElementTheme.Default;
    public const BackdropMaterial BackdropMaterial = BackdropMaterial.Mica;
    public const bool DynamicThemingEnabled = true;
    public const bool RestorePlaybackStateEnabled = true;
    public const bool StartMinimizedEnabled = false;
    public const double Volume = 0.5;
    public const bool MuteState = false;
    public const bool ShuffleState = false;
    public const RepeatMode RepeatMode = Core.Models.RepeatMode.Off;
    public const bool CheckForUpdatesEnabled = true;
    public const bool LastFmScrobblingEnabled = false;
    public const bool LastFmNowPlayingEnabled = false;
    public const bool RememberWindowSizeEnabled = false;
    public const bool RememberPaneStateEnabled = true;
    public const bool VolumeNormalizationEnabled = false;
    public const float EqualizerPreamp = 0.0f;
    public static readonly Color? AccentColor = null;
}
