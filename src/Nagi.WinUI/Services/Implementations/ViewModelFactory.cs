using System;
using Microsoft.Extensions.DependencyInjection;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

public sealed class ViewModelFactory : IViewModelFactory
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
