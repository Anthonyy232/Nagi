using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Lists;
using Nagi.Presentation.Resources;

namespace Nagi.WinUI.Controls;

/// <summary>
///     The bar above a song list: its title with the song or selection count, search, sort, and the play buttons.
/// </summary>
public sealed partial class SongListToolbar : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(SongListViewModelBase), typeof(SongListToolbar), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SongListToolbar), new PropertyMetadata(Strings.SongList_Songs));

    public static readonly DependencyProperty SearchPlaceholderProperty = DependencyProperty.Register(
        nameof(SearchPlaceholder), typeof(string), typeof(SongListToolbar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SearchToolTipProperty = DependencyProperty.Register(
        nameof(SearchToolTip), typeof(string), typeof(SongListToolbar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ExtraContentProperty = DependencyProperty.Register(
        nameof(ExtraContent), typeof(object), typeof(SongListToolbar), new PropertyMetadata(null));

    public SongListToolbar()
    {
        InitializeComponent();
        Loaded += (_, _) => VisualStateManager.GoToState(this, IsPageTitle ? "PageTitle" : "SectionTitle", false);
    }

    public SongListViewModelBase? ViewModel
    {
        get => (SongListViewModelBase?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Shows the title as the page's own title, for a page that is only the song list.</summary>
    public bool IsPageTitle { get; set; }

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

    /// <summary>Shown before the play buttons, such as a smart playlist's edit button.</summary>
    public object? ExtraContent
    {
        get => GetValue(ExtraContentProperty);
        set => SetValue(ExtraContentProperty, value);
    }
}
