using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.OnlineProviders;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Settings;
using CoreStrings = Nagi.Core.Resources.Strings;

namespace Nagi.Presentation.Lists;

public sealed class ArtistAlbumViewModelItem
{
    public ArtistAlbumViewModelItem(Album album)
    {
        Id = album.Id;
        Name = album.Title;
        YearText = album.Year?.ToString() ?? string.Empty;
        CoverArtUri = album.CoverArtUri;
    }

    public Guid Id { get; }
    public string Name { get; }
    public string? CoverArtUri { get; }
    public string YearText { get; }

    public override string ToString() => Name;
}

public partial class ArtistViewViewModel : DetailSongListViewModelBase<ArtistViewNavigationParameter>
{
    private readonly IArtworkService _artwork;
    private Guid _artistId;

    public ArtistViewViewModel(
        ILibraryReader libraryReader,
        IListeningService listening,
        ILibraryNotifier libraryNotifier,
        IArtworkService artwork,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        ISettingsService settingsService,
        IDispatcherService dispatcherService,
        IUIService uiService,
        ILogger<ArtistViewViewModel> logger)
        : base(libraryReader, listening, libraryNotifier, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger, SortOrders, AppSettings.ArtistViewSortOrder)
    {
        _artwork = artwork;
        PageTitle = Strings.Generic_DefaultArtistName;
        ArtistBio = Strings.Status_Loading;
        Albums.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAlbums));
        _libraryNotifier.ArtistImagesChanged += OnArtistImagesChanged;
    }

    private static readonly SongSortOrder[] SortOrders =
    [
        SongSortOrder.TitleAsc, SongSortOrder.TitleDesc, SongSortOrder.YearAsc, SongSortOrder.YearDesc,
        SongSortOrder.AlbumAsc, SongSortOrder.AlbumDesc, SongSortOrder.FileCreatedDateAsc, SongSortOrder.FileCreatedDateDesc
    ];

    [ObservableProperty] public partial string ArtistBio { get; set; }

    /// <summary>The credit a biography's license asks for, or null if it needs none.</summary>
    [ObservableProperty] public partial string? BiographyCredit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomImage))]
    public partial string? ArtistImageUri { get; set; }

    public bool IsCustomImage => ImageStorageHelper.IsCustomImage(ArtistImageUri);

    public ObservableRangeCollection<ArtistAlbumViewModelItem> Albums { get; } = new();
    public bool HasAlbums => Albums.Count > 0;

    protected override SongScope? Scope => _artistId != Guid.Empty ? new SongScope.Artist(_artistId) : null;

    // The details can wait on an online lookup, so the songs don't wait for them.
    protected override bool LoadsSongsWithDetails => true;

    protected override string NotFoundTitle => Strings.ArtistView_ArtistNotFound;

    protected override string OpenErrorTitle => Strings.ArtistView_Error;

    protected override async Task<bool> OpenDetailsAsync(ArtistViewNavigationParameter parameter, CancellationToken token)
    {
        _artistId = parameter.ArtistId;
        if (await _artwork.GetArtistDetailsAsync(parameter.ArtistId, token) is not { } artist) return false;
        if (token.IsCancellationRequested) return true;

        PageTitle = artist.Name;
        ArtistImageUri = artist.ImageUri;
        ArtistBio = string.IsNullOrWhiteSpace(artist.Biography) ? Strings.ArtistView_NoBiography : artist.Biography;
        BiographyCredit = artist.BiographySource == ServiceProviderIds.Wikipedia && !string.IsNullOrWhiteSpace(artist.Biography)
            ? CoreStrings.Biography_WikipediaAttribution
            : null;
        return true;
    }

    // With the songs, so the albums follow rescans as well. A search doesn't change them.
    protected override async Task OnAuxiliaryLoadAsync(CancellationToken token)
    {
        await base.OnAuxiliaryLoadAsync(token);
        if (_artistId == Guid.Empty || IsSearchActive) return;

        var albums = await _libraryReader.GetAlbumsForArtistAsync(_artistId);
        await _dispatcherService.EnqueueAsync(() =>
        {
            if (!token.IsCancellationRequested && !_isDisposed)
                Albums.ReplaceRange(albums.Select(album => new ArtistAlbumViewModelItem(album)));
            return Task.CompletedTask;
        });
    }

    protected override void ClearDetails()
    {
        ArtistBio = string.Empty;
        BiographyCredit = null;
        ArtistImageUri = null;
        Albums.Clear();
    }

    public void OpenAlbum(ArtistAlbumViewModelItem album) => _musicNavigationService.OpenAlbum(album.Id);

    [RelayCommand]
    private Task PlayAlbumAsync(Guid albumId) => RunActionAsync(() => _playbackService.PlayScopeAsync(new SongScope.Album(albumId)));

    private void OnArtistImagesChanged(object? sender, IReadOnlyList<ArtistImageChange> changes)
    {
        if (changes.FirstOrDefault(change => change.ArtistId == _artistId) is not { } change) return;
        var token = OpenToken;
        _dispatcherService.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested || _isDisposed) return;
            ArtistImageUri = change.ImageUri;
        });
    }

    public override SongListColumns VisibleColumns => ChosenColumns & ~SongListColumns.Artist;

    public override bool ShowsGoToArtist => false;

    public override void Dispose()
    {
        _libraryNotifier.ArtistImagesChanged -= OnArtistImagesChanged;
        base.Dispose();
    }

    [RelayCommand]
    private Task ChangeArtistImageAsync() =>
        ArtistImageActions.ChangeAsync(_artistId, _uiService, _artwork, _logger, () => _isDisposed);

    [RelayCommand]
    private Task RemoveArtistImageAsync() =>
        ArtistImageActions.RemoveAsync(_artistId, _uiService, _artwork, _logger, () => _isDisposed);
}
