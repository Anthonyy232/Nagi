using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Navigation;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Opens detail pages in the frame that <see cref="MainPage" /> hands over through <see cref="Initialize" />.
/// </summary>
public class NavigationService : INavigationService
{
    private readonly ILogger<NavigationService> _logger;
    private Frame? _frame;

    public NavigationService(ILogger<NavigationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     The parameter the frame's current page was opened with.
    /// </summary>
    public object? CurrentParameter { get; private set; }

    public void Initialize(Frame frame)
    {
        if (_frame != null) _frame.Navigated -= OnFrameNavigated;
        CurrentParameter = null;
        _frame = frame;
        _frame.Navigated += OnFrameNavigated;
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

        if (Equals(CurrentParameter, parameter))
        {
            _logger.LogDebug("Navigation to same page {PageName} prevented.", pageType.Name);
            return;
        }

        _frame.Navigate(pageType, parameter);
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs e) => CurrentParameter = e.Parameter;
}
