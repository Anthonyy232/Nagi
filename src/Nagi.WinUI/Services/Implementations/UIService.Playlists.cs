using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nagi.Core.Constants;
using Nagi.Presentation.Models;
using Nagi.Presentation.Resources;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Services.Implementations;

public partial class UIService
{
    public async Task<NewPlaylistRequest?> ShowCreatePlaylistDialogAsync()
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        string? coverImagePath = null;
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
            CloseButtonText = Strings.PlaylistPage_CreateDialog_CancelButton,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = xamlRoot
        };

        pickImageButton.Click += async (_, _) =>
        {
            try
            {
                var pickedPath = await PickSingleFileAsync(FileExtensions.ImageFileExtensions);
                if (string.IsNullOrWhiteSpace(pickedPath)) return;

                coverImagePath = pickedPath;
                imagePreview.Source = BitmapImageFactory.Create(pickedPath);
                imagePlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error picking cover image for new playlist");
            }
        };
        inputTextBox.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(inputTextBox.Text);

        DialogThemeHelper.ApplyThemeOverrides(dialog);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? new NewPlaylistRequest(inputTextBox.Text, coverImagePath) : null;
    }

    public async Task<string?> ShowRenamePlaylistDialogAsync(string currentName)
    {
        if (!TryGetXamlRoot(out var xamlRoot)) return null;

        var inputTextBox = new TextBox { Text = currentName };
        var dialog = new ContentDialog
        {
            Title = string.Format(Strings.PlaylistPage_RenameDialog_Title_Format, currentName),
            Content = inputTextBox,
            PrimaryButtonText = Strings.PlaylistPage_RenameDialog_RenameButton,
            CloseButtonText = Strings.PlaylistPage_CreateDialog_CancelButton,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = xamlRoot
        };

        inputTextBox.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(inputTextBox.Text) &&
                                            inputTextBox.Text.Trim() != currentName;

        DialogThemeHelper.ApplyThemeOverrides(dialog);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? inputTextBox.Text : null;
    }
}
