namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
/// Creates page-owned ViewModels that the page is responsible for disposing.
/// </summary>
public interface IViewModelFactory
{
    T Create<T>() where T : class;
}
