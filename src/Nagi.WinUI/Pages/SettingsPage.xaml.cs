using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page for configuring application settings.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly ILogger<SettingsPage> _logger;

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<SettingsViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<SettingsPage>>();
        DataContext = ViewModel;
        _logger.LogDebug("SettingsPage initialized.");
    }

    public SettingsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogDebug("Navigated to SettingsPage. Loading settings...");
        try
        {
            await ViewModel.LoadSettingsAsync();
            _logger.LogDebug("Settings loaded successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from SettingsPage.");
    }

    /// <summary>
    ///     Handles pointer entered on player button items - shows drag indicator and toggle with smooth animation.
    /// </summary>
    private void PlayerButtonItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid container) return;

        // Find and animate the drag indicator
        if (FindDescendantByName(container, "DragIndicator") is UIElement dragIndicator)
        {
            AnimateOpacity(dragIndicator, 1.0, TimeSpan.FromMilliseconds(150));
        }

        // Find and animate the toggle switch
        if (FindDescendantByName(container, "EnableDisableToggle") is UIElement toggle)
        {
            AnimateOpacity(toggle, 1.0, TimeSpan.FromMilliseconds(150));
        }

        // Add subtle background highlight
        if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var resource)
            && resource is Brush brush)
        {
            container.Background = brush;
        }
    }

    /// <summary>
    ///     Handles pointer exited on player button items - hides drag indicator and toggle with smooth animation.
    /// </summary>
    private void PlayerButtonItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid container) return;

        // Find and animate the drag indicator
        if (FindDescendantByName(container, "DragIndicator") is UIElement dragIndicator)
        {
            AnimateOpacity(dragIndicator, 0.0, TimeSpan.FromMilliseconds(100));
        }

        // Find and animate the toggle switch
        if (FindDescendantByName(container, "EnableDisableToggle") is UIElement toggle)
        {
            AnimateOpacity(toggle, 0.0, TimeSpan.FromMilliseconds(100));
        }

        // Remove background highlight
        container.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>
    ///     Animates the opacity of a UI element smoothly.
    /// </summary>
    private static void AnimateOpacity(UIElement element, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Begin();
    }

    /// <summary>
    ///     Finds a descendant element by its x:Name in the visual tree.
    /// </summary>
    private static UIElement? FindDescendantByName(DependencyObject parent, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name)
            {
                return fe;
            }

            var result = FindDescendantByName(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
