using Windows.ApplicationModel.DataTransfer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Nagi.WinUI.Controls;

public sealed partial class CrashReportDialogContent : UserControl
{
    private readonly ILogger<CrashReportDialogContent>? _logger;

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
        _logger = App.Services?.GetService<ILogger<CrashReportDialogContent>>();
    }

    public string Introduction
    {
        get => (string)GetValue(IntroductionProperty);
        set => SetValue(IntroductionProperty, value);
    }

    public string LogContent
    {
        get => (string)GetValue(LogContentProperty);
        set => SetValue(LogContentProperty, value);
    }

    public string GitHubUrl
    {
        get => (string)GetValue(GitHubUrlProperty);
        set => SetValue(GitHubUrlProperty, value);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(LogContent);
            Clipboard.SetContent(dataPackage);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to copy crash log.");
        }
    }
}
