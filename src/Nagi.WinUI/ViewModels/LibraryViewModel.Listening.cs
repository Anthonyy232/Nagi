using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nagi.Core.Models;
using Nagi.WinUI.Resources;

namespace Nagi.WinUI.ViewModels;

public partial class LibraryViewModel
{

    [ObservableProperty]
    public partial bool ShowFavoritesOnly { get; set; }

    partial void OnShowFavoritesOnlyChanged(bool value)
    {
        _pagedLoadCts?.Cancel();
        DeselectAll();
        ClearFullSongIds();
        CurrentPage = 1;
        _ = RefreshOrSortSongsAsync();
    }
}
