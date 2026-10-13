using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Opens detail pages in the frame that <see cref="MainPage" /> hands over through <see cref="Initialize" />.
/// </summary>
public class NavigationService : INavigationService, IDisposable
{
    private static readonly Dictionary<Type, Type> DetailPages = new()
    {
        [typeof(AlbumViewNavigationParameter)] = typeof(AlbumViewPage),
        [typeof(ArtistViewNavigationParameter)] = typeof(ArtistViewPage),
        [typeof(FolderSongViewNavigationParameter)] = typeof(FolderSongViewPage),
        [typeof(GenreViewNavigationParameter)] = typeof(GenreViewPage),
        [typeof(PlaylistSongViewNavigationParameter)] = typeof(PlaylistSongViewPage),
        [typeof(SmartPlaylistSongViewNavigationParameter)] = typeof(SmartPlaylistSongViewPage),
    };

    private readonly ILogger<NavigationService> _logger;

    private Frame? _frame;
    private bool _isDisposed;
    private Type? _lastPageType;
    private object? _lastParameter;

    public NavigationService(ILogger<NavigationService> logger)
    {
        _logger = logger;
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        if (_frame != null) _frame.Navigated -= OnFrameNavigated;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    public void Initialize(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_frame != null) _frame.Navigated -= OnFrameNavigated;
        _lastPageType = null;
        _lastParameter = null;
        _frame = frame;
        _frame.Navigated += OnFrameNavigated;
    }

    private void Navigate(Type pageType, INavigationParameter parameter)
    {
        if (_frame is null)
        {
            _logger.LogError("Attempted to navigate before the root frame was initialized.");
            return;
        }

        // Prevent navigating to the same page with the same parameter, which is usually redundant.
        if (_lastPageType == pageType && Equals(_lastParameter, parameter))
        {
            _logger.LogDebug("Navigation to same page {PageName} prevented.", pageType.Name);
            return;
        }

        _frame.Navigate(pageType, parameter);
    }

    public void NavigateTo(INavigationParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (!DetailPages.TryGetValue(parameter.GetType(), out var pageType))
            throw new ArgumentException($"No page accepts {parameter.GetType().Name}.", nameof(parameter));
        Navigate(pageType, parameter);
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        _lastPageType = e.SourcePageType;
        _lastParameter = e.Parameter;
    }
}
