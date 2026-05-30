using CommunityToolkit.Mvvm.ComponentModel;
using Nagi.Core.Models.Romanization;

namespace Nagi.WinUI.ViewModels;

public partial class RomanizationPackViewModel : ObservableObject
{
    private readonly RomanizationPackView _packView;

    public RomanizationPackViewModel(RomanizationPackView packView)
    {
        _packView = packView;
    }

    public string Id => _packView.CatalogEntry.Id;
    public string DisplayName => _packView.CatalogEntry.DisplayName;
    public string Description => _packView.CatalogEntry.Description;
    public string Language => _packView.CatalogEntry.Language;
    public string Script => _packView.CatalogEntry.Script;
    public string Version => _packView.CatalogEntry.Version;
    public string InstalledVersion => _packView.InstalledPack?.Manifest.Version ?? string.Empty;
    public string License => _packView.CatalogEntry.License;
    public string Attribution => _packView.CatalogEntry.Attribution;
    public string LicenseText => string.IsNullOrWhiteSpace(License)
        ? string.Empty
        : string.Format(Nagi.WinUI.Resources.Strings.SettingsPage_RomanizationPack_LicenseFormat, License);
    public string SizeText => FormatSize(_packView.CatalogEntry.SizeBytes);
    public string DetailsText
    {
        get
        {
            var parts = new[] { Language, Script, SizeText, LicenseText }
                .Where(part => !string.IsNullOrWhiteSpace(part));

            return string.Join("  |  ", parts);
        }
    }

    public bool IsInstalled => _packView.IsInstalled;
    public bool IsUpdateAvailable => _packView.IsUpdateAvailable;
    public bool CanInstall => !IsBusy && (!IsInstalled || IsUpdateAvailable);
    public bool CanRemove => !IsBusy && IsInstalled;

    public string StatusText
    {
        get
        {
            if (IsUpdateAvailable) return Nagi.WinUI.Resources.Strings.SettingsPage_RomanizationPack_UpdateAvailable;
            if (IsInstalled) return string.Format(Nagi.WinUI.Resources.Strings.SettingsPage_RomanizationPack_InstalledFormat, InstalledVersion);
            return Nagi.WinUI.Resources.Strings.SettingsPage_RomanizationPack_NotInstalled;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    public partial bool IsBusy { get; set; }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        var mb = bytes / 1024d / 1024d;
        return mb >= 1 ? $"{mb:0.#} MB" : $"{bytes / 1024d:0.#} KB";
    }
}
