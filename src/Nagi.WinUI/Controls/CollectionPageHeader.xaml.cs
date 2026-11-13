using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Controls;

/// <summary>
///     The header of a collection page: its title and count, search, sort, and the play-a-random-item button.
/// </summary>
public sealed partial class CollectionPageHeader : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ICollectionViewModel), typeof(CollectionPageHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(CollectionPageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CountTextProperty = DependencyProperty.Register(
        nameof(CountText), typeof(string), typeof(CollectionPageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SearchPlaceholderProperty = DependencyProperty.Register(
        nameof(SearchPlaceholder), typeof(string), typeof(CollectionPageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SearchToolTipProperty = DependencyProperty.Register(
        nameof(SearchToolTip), typeof(string), typeof(CollectionPageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ExtraContentProperty = DependencyProperty.Register(
        nameof(ExtraContent), typeof(object), typeof(CollectionPageHeader), new PropertyMetadata(null));

    public CollectionPageHeader()
    {
        InitializeComponent();
    }

    public ICollectionViewModel? ViewModel
    {
        get => (ICollectionViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>How many items there are, shown next to the title; empty to show none.</summary>
    public string CountText
    {
        get => (string)GetValue(CountTextProperty);
        set => SetValue(CountTextProperty, value);
    }

    /// <summary>Search and sort show only for a collection with a <see cref="ICollectionViewModel.Sort" />.</summary>
    public string SearchPlaceholder
    {
        get => (string)GetValue(SearchPlaceholderProperty);
        set => SetValue(SearchPlaceholderProperty, value);
    }

    public string SearchToolTip
    {
        get => (string)GetValue(SearchToolTipProperty);
        set => SetValue(SearchToolTipProperty, value);
    }

    /// <summary>Shown after the random button, such as a page's add button.</summary>
    public object? ExtraContent
    {
        get => GetValue(ExtraContentProperty);
        set => SetValue(ExtraContentProperty, value);
    }
}
