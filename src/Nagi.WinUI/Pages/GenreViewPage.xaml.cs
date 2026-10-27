using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Implementations;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class GenreViewPage : SongListPage
{

    public GenreViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ViewModelFactory>().Create<GenreViewViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ownsViewModel: true, SearchToggleButton, SearchTextBox,
            Strings.GenreViewPage_SearchButton_Search_ToolTip, ColumnHeaderHost, SongsListView);
    }

    public GenreViewViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var navParam = (GenreViewNavigationParameter)e.Parameter;
        await ViewModel.LoadGenreDetailsAsync(navParam);
    }
}
