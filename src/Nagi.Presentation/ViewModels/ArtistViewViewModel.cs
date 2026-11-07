using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Metadata;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Models;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;

namespace Nagi.Presentation.ViewModels;

public sealed class ArtistAlbumViewModelItem
{
    public ArtistAlbumViewModelItem(Album album)
    {
        Id = album.Id;
        Name = album.Title;
        YearText = album.Year?.ToString() ?? string.Empty;
        CoverArtUri = ImageUriHelper.GetUriWithCacheBuster(album.CoverArtUri);
    }

    public Guid Id { get; }
    public string Name { get; }
    public string? CoverArtUri { get; }
    public string YearText { get; }
    public bool IsArtworkAvailable => !string.IsNullOrEmpty(CoverArtUri);

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
        PageTitle = Strings.ArtistView_DefaultArtistName;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomImage))]
    public partial string? ArtistImageUri { get; set; }

    public bool IsCustomImage => ImageStorageHelper.IsCustomImage(ArtistImageUri);

    public ObservableRangeCollection<ArtistAlbumViewModelItem> Albums { get; } = new();
    public bool HasAlbums => Albums.Any();

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (_artistId == Guid.Empty) return Task.FromResult(new PagedResult<Song>());

        return _libraryReader.GetSongsAsync(new SongScope.Artist(_artistId), SearchTerm, sortOrder, pageNumber, pageSize,
            cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (_artistId == Guid.Empty) return Task.FromResult(new List<Guid>());

        return _libraryReader.GetSongIdsAsync(new SongScope.Artist(_artistId), SearchTerm, sortOrder, token);
    }

    // The details can wait on an online lookup, so the songs don't wait for them.
    protected override bool LoadsSongsWithDetails => true;

    protected override string NotFoundTitle => Strings.ArtistView_ArtistNotFound;

    protected override string OpenErrorTitle => Strings.ArtistView_Error;

    protected override async Task<bool> OpenDetailsAsync(ArtistViewNavigationParameter parameter, CancellationToken token)
    {
        _artistId = parameter.ArtistId;
        var artistTask = _artwork.GetArtistDetailsAsync(parameter.ArtistId, token);
        var albumsTask = _libraryReader.GetAlbumsForArtistAsync(parameter.ArtistId);
        await Task.WhenAll(artistTask, albumsTask);
        if (artistTask.Result is not { } artist || token.IsCancellationRequested) return artistTask.Result is not null;

        PageTitle = artist.Name;
        ArtistImageUri = ImageUriHelper.GetUriWithCacheBuster(artist.LocalImageCachePath);
        ArtistBio = string.IsNullOrWhiteSpace(artist.Biography) ? Strings.ArtistView_NoBiography : artist.Biography;
        Albums.ReplaceRange(albumsTask.Result.Select(album => new ArtistAlbumViewModelItem(album)));
        return true;
    }

    protected override void ClearDetails()
    {
        ArtistBio = string.Empty;
        ArtistImageUri = null;
        Albums.Clear();
    }

    public void OpenAlbum(ArtistAlbumViewModelItem album) => _musicNavigationService.OpenAlbum(album.Id);

    [RelayCommand]
    private Task PlayAlbumAsync(Guid albumId) => RunActionAsync(() => _playbackService.PlayAlbumAsync(albumId));

    private void OnArtistImagesChanged(object? sender, IReadOnlyList<ArtistImageChange> changes)
    {
        if (changes.FirstOrDefault(change => change.ArtistId == _artistId) is not { } change) return;
        var token = OpenToken;
        _dispatcherService.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested || _isDisposed) return;
            ArtistImageUri = ImageUriHelper.GetUriWithCacheBuster(change.ImagePath, DateTime.UtcNow);
        });
    }

    public override SongListColumns VisibleColumns => ChosenColumns & ~SongListColumns.Artist;

    public override bool ShowsGoToArtist => false;

    protected override PlaybackContext GetPlaybackContext() =>
        _artistId != Guid.Empty ? new(PlaybackContextType.Artist, _artistId) : base.GetPlaybackContext();

    public override void Dispose()
    {
        _libraryNotifier.ArtistImagesChanged -= OnArtistImagesChanged;
        base.Dispose();
    }

    /// <summary>
    ///     Lets the user pick an image file and stores it as this artist's custom image.
    /// </summary>
    [RelayCommand]
    private async Task ChangeArtistImageAsync()
    {
        try
        {
            var localPath = await _uiService.PickSingleFileAsync(FileExtensions.ImageFileExtensions);
            if (string.IsNullOrWhiteSpace(localPath)) return;

            _logger.LogInformation("Updating custom image for current artist ({ArtistName}) from {Path}", PageTitle, localPath);
            await _artwork.UpdateArtistImageAsync(_artistId, localPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change the image of artist {ArtistName}", PageTitle);
        }
    }

    [RelayCommand]
    private async Task RemoveArtistImageAsync()
    {
        _logger.LogInformation("Removing custom image for current artist ({ArtistName})", PageTitle);
        try { await _artwork.RemoveArtistImageAsync(_artistId); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to remove the image of artist {ArtistName}", PageTitle); }
    }
}
