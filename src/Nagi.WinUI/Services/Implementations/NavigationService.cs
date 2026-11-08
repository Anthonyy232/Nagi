using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Navigation;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Opens menu and detail pages in the frame that <see cref="MainPage" /> hands over through <see cref="Initialize" />.
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
    ///     The menu item the current page belongs to, or null when no menu item leads there.
    /// </summary>
    public string? CurrentMenuTag => AppDestinations.GetMenuTag(_frame?.CurrentSourcePageType, _currentParameter);

    public bool IsOnDetailPage => _frame?.CurrentSourcePageType is { } page && AppDestinations.IsDetailPage(page);

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

    private void OnFrameNavigated(object sender, NavigationEventArgs e) => _currentParameter = e.Parameter;
}
