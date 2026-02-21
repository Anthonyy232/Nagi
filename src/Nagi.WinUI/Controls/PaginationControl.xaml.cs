using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Controls;

public sealed partial class PaginationControl : UserControl
{
    public PaginationControl()
    {
        this.InitializeComponent();
    }

    public int[] PageSizeOptions { get; } = new[] { 25, 50, 100, 250, 500 };

    public SongListViewModelBase ViewModel
    {
        get => (SongListViewModelBase)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(SongListViewModelBase), typeof(PaginationControl), new PropertyMetadata(null));
}
