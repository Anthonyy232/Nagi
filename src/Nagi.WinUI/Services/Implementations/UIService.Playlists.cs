using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nagi.Core.Constants;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    public async Task<NewPlaylistRequest?> ShowCreatePlaylistDialogAsync()
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        var viewModel = new PlaylistNameDialogViewModel();
        var inputTextBox = new TextBox { PlaceholderText = Strings.PlaylistPage_CreateDialog_Placeholder };
        var imagePreview = new ImageEx.ImageEx
        {
            Stretch = Stretch.UniformToFill,
            IsCacheEnabled = true,
            DecodePixelWidth = 80,
            DecodePixelType = DecodePixelType.Logical
        };
        var imagePlaceholder = new FontIcon { Glyph = "", FontSize = 48 };
        var imageGrid = new Grid
        {
            Width = 80,
            Height = 80,
            CornerRadius = (CornerRadius)Application.Current.Resources["ArtworkCornerRadius"],
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        imageGrid.Children.Add(imagePlaceholder);
        imageGrid.Children.Add(imagePreview);
        var pickImageButton = new Button
        {
            Content = Strings.PlaylistPage_CreateDialog_PickImage,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var dialogContent = new StackPanel();
        dialogContent.Children.Add(imageGrid);
        dialogContent.Children.Add(inputTextBox);
        dialogContent.Children.Add(pickImageButton);

        var dialog = new ContentDialog
        {
            Title = Strings.PlaylistPage_CreateDialog_Title,
            Content = dialogContent,
            PrimaryButtonText = Strings.PlaylistPage_CreateDialog_CreateButton,
            CloseButtonText = Strings.Generic_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        pickImageButton.Click += async (_, _) =>
        {
            var pickedPath = await PickSingleFileAsync(FileExtensions.ImageFileExtensions);
            if (string.IsNullOrWhiteSpace(pickedPath)) return;

            viewModel.CoverImagePath = pickedPath;
            imagePreview.Source = BitmapImageFactory.Create(pickedPath);
            imagePlaceholder.Visibility = Visibility.Collapsed;
        };
        TrackName(inputTextBox, dialog, viewModel);

        var result = await ShowDialogAsync(dialog);
        return result == ContentDialogResult.Primary
            ? new NewPlaylistRequest(viewModel.Name, viewModel.CoverImagePath)
            : null;
    }

    public async Task<string?> ShowRenamePlaylistDialogAsync(string currentName)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        var viewModel = new PlaylistNameDialogViewModel(currentName);
        var inputTextBox = new TextBox { Text = viewModel.Name };
        var dialog = new ContentDialog
        {
            Title = ResourceFormatter.Format(Strings.PlaylistPage_RenameDialog_Title_Format, currentName),
            Content = inputTextBox,
            PrimaryButtonText = Strings.PlaylistPage_RenameDialog_RenameButton,
            CloseButtonText = Strings.Generic_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };
        TrackName(inputTextBox, dialog, viewModel);

        var result = await ShowDialogAsync(dialog);
        return result == ContentDialogResult.Primary ? viewModel.Name : null;
    }

    private static void TrackName(TextBox input, ContentDialog dialog, PlaylistNameDialogViewModel viewModel)
    {
        dialog.IsPrimaryButtonEnabled = viewModel.CanAccept;
        input.TextChanged += (_, _) =>
        {
            viewModel.Name = input.Text;
            dialog.IsPrimaryButtonEnabled = viewModel.CanAccept;
        };
    }
}
