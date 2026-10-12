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
///     Provides a service for navigating between pages within a XAML Frame.
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

    /// <summary>
    ///     Cleans up resources by unsubscribing from the Frame's Navigated event.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        if (_frame != null) _frame.Navigated -= OnFrameNavigated;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Initializes the navigation service with the application's root frame.
    /// </summary>
    /// <param name="frame">The root frame used for navigation.</param>
    public void Initialize(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_frame != null) _frame.Navigated -= OnFrameNavigated;
        _lastPageType = null;
        _lastParameter = null;
        _frame = frame;
        _frame.Navigated += OnFrameNavigated;
    }

    /// <summary>
    ///     Navigates to the specified page type.
    /// </summary>
    /// <param name="pageType">The type of the page to navigate to.</param>
    /// <param name="parameter">An optional parameter to pass to the target page.</param>
    public void Navigate(Type pageType, object? parameter = null)
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

    /// <inheritdoc />
    public void NavigateTo(object parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (!DetailPages.TryGetValue(parameter.GetType(), out var pageType))
            throw new ArgumentException($"No page accepts {parameter.GetType().Name}.", nameof(parameter));
        Navigate(pageType, parameter);
    }

    /// <summary>
    ///     Updates the service's state after a navigation has completed.
    /// </summary>
    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        _lastPageType = e.SourcePageType;
        _lastParameter = e.Parameter;
    }
}
