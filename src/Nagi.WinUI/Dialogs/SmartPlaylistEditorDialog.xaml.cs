using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Dialogs;

/// <summary>
///     The view for <see cref="SmartPlaylistEditorViewModel" />: creates or edits a smart playlist.
/// </summary>
public sealed partial class SmartPlaylistEditorDialog : ContentDialog
{
    public SmartPlaylistEditorDialog(SmartPlaylistEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DialogThemeHelper.ApplyThemeOverrides(this);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnDialogClosed;
        UpdateCoverImage();
    }

    public SmartPlaylistEditorViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SmartPlaylistEditorViewModel.CoverImageUri)) UpdateCoverImage();
    }

    private void UpdateCoverImage()
    {
        CoverImagePreview.Source = ViewModel.HasCoverImage
            ? BitmapImageFactory.Create(ImageUriHelper.GetUriWithCacheBuster(ViewModel.CoverImageUri))
            : null;
    }

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Closed -= OnDialogClosed;
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RuleViewModel rule }) ViewModel.RemoveRuleCommand.Execute(rule);
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var result = await ViewModel.SaveAsync();
            if (result == SmartPlaylistSaveResult.Saved) return;

            args.Cancel = true;
            if (result == SmartPlaylistSaveResult.NameMissing)
                PlaylistNameTextBox.PlaceholderText = Nagi.Presentation.Resources.Strings.SmartPlaylist_Error_NameEmptyFeedback;
            if (result is SmartPlaylistSaveResult.NameMissing or SmartPlaylistSaveResult.DuplicateName)
                PlaylistNameTextBox.Focus(FocusState.Programmatic);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
