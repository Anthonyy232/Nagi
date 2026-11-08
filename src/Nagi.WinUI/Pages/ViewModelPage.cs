using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page the navigation frame creates. The frame needs a parameterless constructor, so the page gets its view
///     model from <see cref="GetViewModel{T}" />: a singleton is shared, and a view model registered as scoped
///     belongs to this page and is disposed when the frame lets go of the page.
/// </summary>
public abstract class ViewModelPage : Page
{
    private static IServiceScopeFactory? _scopeFactory;
    private IServiceScope? _scope;

    /// <summary>Called once at startup, before the frame creates any page.</summary>
    internal static void Initialize(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    protected T GetViewModel<T>() where T : notnull
    {
        _scope ??= _scopeFactory!.CreateScope();
        return _scope.ServiceProvider.GetRequiredService<T>();
    }

    // A page the frame caches is shown again, so what it owns lives as long as the page.
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (NavigationCacheMode != NavigationCacheMode.Disabled) return;
        _scope?.Dispose();
        _scope = null;
    }
}
