using Microsoft.Extensions.DependencyInjection;

namespace Nagi.Presentation.Services.Implementations;

/// <summary>
///     Creates page-owned view models that the page is responsible for disposing.
/// </summary>
public sealed class ViewModelFactory
{
    private readonly IServiceProvider _services;

    public ViewModelFactory(IServiceProvider services)
    {
        _services = services;
    }

    public T Create<T>() where T : class
    {
        // ActivatorUtilities resolves dependencies without registering the page-owned
        // ViewModel as a disposable transient retained by the root service provider.
        return ActivatorUtilities.CreateInstance<T>(_services);
    }
}
