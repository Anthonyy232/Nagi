using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Navigation;
using Nagi.WinUI.Pages;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Opens pages in the frame that <see cref="MainPage" /> hands over through <see cref="Initialize" />.
/// </summary>
public class NavigationService : INavigationService
{
    private readonly ILogger<NavigationService> _logger;
    private Frame? _frame;
    private object? _currentParameter;

    public NavigationService(ILogger<NavigationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Raised after each navigation, once this service's state describes the new page.
    /// </summary>
    public event Action<Type>? Navigated;

    /// <summary>
    ///     The menu item the current page belongs to, or null when no menu item leads there.
    /// </summary>
    public string? CurrentMenuTag => AppDestinations.GetMenuTag(_frame?.CurrentSourcePageType, _currentParameter);

    public object? CurrentParameter => _currentParameter;

    public bool ShowsBackButton =>
        _frame is { CanGoBack: true, CurrentSourcePageType: { } page } && AppDestinations.ShowsBackButton(page);

    public void Initialize(Frame frame)
    {
        if (_frame != null) _frame.Navigated -= OnFrameNavigated;
        _currentParameter = null;
        _frame = frame;
        _frame.Navigated += OnFrameNavigated;
    }

    public void NavigateToMenu(string tag, NavigationTransitionInfo transition)
    {
        if (_frame is null || !AppDestinations.TryGetMenuPage(tag, out var pageType, out var parameter)) return;
        if (_frame.CurrentSourcePageType != pageType || !Equals(_currentParameter, parameter))
            _frame.Navigate(pageType, parameter, transition);
    }

    public void NavigateTo(INavigationParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var pageType = AppDestinations.GetDetailPage(parameter);

        if (_frame is null)
        {
            _logger.LogError("Attempted to navigate before the root frame was initialized.");
            return;
        }

        if (Equals(_currentParameter, parameter))
        {
            _logger.LogDebug("Navigation to same page {PageName} prevented.", pageType.Name);
            return;
        }

        _frame.Navigate(pageType, parameter);
    }

    /// <summary>
    ///     Opens the lyrics page, or leaves it when it is already open.
    /// </summary>
    public void ToggleLyrics()
    {
        if (_frame?.CurrentSourcePageType == typeof(LyricsPage)) GoBack();
        else _frame?.Navigate(typeof(LyricsPage));
    }

    public void GoBack()
    {
        if (_frame is { CanGoBack: true }) _frame.GoBack();
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        _currentParameter = e.Parameter;
        _logger.LogDebug("Navigated to {PageName}.", e.SourcePageType.Name);
        Navigated?.Invoke(e.SourcePageType);
    }
}
