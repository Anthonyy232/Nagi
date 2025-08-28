using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A UserControl that provides the content for a crash report dialog.
///     It displays an introduction, a link to report an issue, and the log content.
/// </summary>
public sealed partial class CrashReportDialogContent : UserControl
{
    public static readonly DependencyProperty IntroductionProperty =
        DependencyProperty.Register(nameof(Introduction), typeof(string), typeof(CrashReportDialogContent),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LogContentProperty =
        DependencyProperty.Register(nameof(LogContent), typeof(string), typeof(CrashReportDialogContent),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty GitHubUrlProperty =
        DependencyProperty.Register(nameof(GitHubUrl), typeof(string), typeof(CrashReportDialogContent),
            new PropertyMetadata(string.Empty));

    public CrashReportDialogContent()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     Gets or sets the introductory message displayed to the user.
    /// </summary>
    public string Introduction
    {
        get => (string)GetValue(IntroductionProperty);
        set => SetValue(IntroductionProperty, value);
    }

    /// <summary>
    ///     Gets or sets the log content to be displayed in the text box.
    /// </summary>
    public string LogContent
    {
        get => (string)GetValue(LogContentProperty);
        set => SetValue(LogContentProperty, value);
    }

    /// <summary>
    ///     Gets or sets the URL for the GitHub issues page.
    /// </summary>
    public string GitHubUrl
    {
        get => (string)GetValue(GitHubUrlProperty);
        set => SetValue(GitHubUrlProperty, value);
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(LogContent);
        Clipboard.SetContent(dataPackage);

        // Provide visual feedback by briefly disabling and re-enabling the button.
        CopyButton.IsEnabled = false;
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        CopyButton.IsEnabled = true;
    }
}