using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Controls;

public sealed partial class PaginationControl : UserControl
{
    public PaginationControl()
    {
        InitializeComponent();
    }

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
