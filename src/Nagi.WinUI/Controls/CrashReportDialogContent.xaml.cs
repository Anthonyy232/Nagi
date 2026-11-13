using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace Nagi.WinUI.Controls;

public sealed partial class CrashReportDialogContent : UserControl
{
    public CrashReportDialogContent(string logContent)
    {
        LogContent = logContent;
        InitializeComponent();
    }

    public string LogContent { get; }

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
            // The app is crashing, so its services may be gone; the static logger still writes.
            Log.Error(ex, "Failed to copy crash log.");
        }
    }
}
