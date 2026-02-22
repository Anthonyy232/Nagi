using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Controls;

public sealed partial class PaginationControl : UserControl
{
    private object? _pendingScrollRevertValue;
    private bool _isRevertingScroll;

    public PaginationControl()
    {
        this.InitializeComponent();

        // Use AddHandler with handledEventsToo:true so we fire even if the ComboBox
        // already marked the event as handled internally.
        PageSizeComboBox.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPageSizeComboBoxPointerWheelChanged),
            handledEventsToo: true);

        PageSizeComboBox.SelectionChanged += OnPageSizeComboBoxSelectionChanged;
    }

    // Propagates the selection to the ViewModel only when the user explicitly picks a value
    // from the open dropdown. Changes while closed (scroll, programmatic binding updates) are
    // never forwarded — scroll changes are reverted, binding updates come in via OneWay.
    private void OnPageSizeComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRevertingScroll) return;

        if (PageSizeComboBox.IsDropDownOpen)
        {
            if (e.AddedItems.Count > 0 && ViewModel is { } vm)
                vm.SongsPerPage = (int)e.AddedItems[0];
            _pendingScrollRevertValue = null;
        }
        else if (e.RemovedItems.Count > 0)
        {
            // Dropdown was closed — change came from a scroll. Save old value for revert.
            _pendingScrollRevertValue = e.RemovedItems[0];
        }
    }

    // SelectionChanged fires before this (the ComboBox already changed its value).
    // Revert to the captured pre-scroll value and eat the event.
    private void OnPageSizeComboBoxPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!PageSizeComboBox.IsDropDownOpen)
        {
            e.Handled = true;
            if (_pendingScrollRevertValue != null)
            {
                _isRevertingScroll = true;
                PageSizeComboBox.SelectedItem = _pendingScrollRevertValue;
                _isRevertingScroll = false;
                _pendingScrollRevertValue = null;
            }
        }
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
