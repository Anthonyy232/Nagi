using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Controls;

public sealed partial class PaginationControl : UserControl
{
    public PaginationControl()
    {
        InitializeComponent();
    }

    public int[] PageSizeOptions { get; } = [25, 50, 100, 250, 500, 0];

    /// <summary>
    ///     Hide the entire control when the total item count fits within the smallest
    ///     page-size tier — pagination is meaningless at that scale.
    /// </summary>
    public Visibility VisibleIfPaginates(int totalItemCount) =>
        totalItemCount > 25 ? Visibility.Visible : Visibility.Collapsed;

    public IPagedListViewModel ViewModel
    {
        get => (IPagedListViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(IPagedListViewModel), typeof(PaginationControl), new PropertyMetadata(null));
}

public sealed class PageSizeComboBox : ComboBox
{
    protected override void OnPointerWheelChanged(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (IsDropDownOpen)
            base.OnPointerWheelChanged(e);
        else
            e.Handled = true;
    }
}
