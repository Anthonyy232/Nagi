using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class GenrePage : SearchablePage
{
    private readonly ILogger<GenrePage> _logger;

    public GenrePage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<GenreViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<GenrePage>>();
        DataContext = ViewModel;
        InitializeSearch(ViewModel, SearchToggleButton, SearchTextBox, Strings.GenrePage_SearchButton_Search_ToolTip);

        _logger.LogDebug("GenrePage initialized.");
    }

    public GenreViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            base.OnNavigatedTo(e);
            _logger.LogDebug("Navigated to GenrePage.");
            await ViewModel.LoadGenresCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate to GenrePage correctly.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from GenrePage.");
        ViewModel.ResetState();
        // Note: ViewModel is Singleton, do not dispose - state persists across navigations
    }

    private void GenresGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GenreSummary clickedGenre)
        {
            _logger.LogDebug("User clicked on genre '{GenreName}'. Navigating to detail view.",
                clickedGenre.Name);
            ViewModel.NavigateToGenreDetail(clickedGenre);
        }
    }
}
