using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class GenrePage : SearchablePage
{
    public GenrePage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<GenreViewModel>();
        DataContext = ViewModel;
        InitializeSearch(ViewModel, SearchToggleButton, SearchTextBox, Strings.GenrePage_SearchButton_Search_ToolTip);
    }

    public GenreViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadGenresCommand.ExecuteAsync(null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.ResetState();
        // Note: ViewModel is Singleton, do not dispose - state persists across navigations
    }

    private void GenresGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GenreSummary clickedGenre)
        {
            ViewModel.NavigateToGenreDetail(clickedGenre);
        }
    }
}
