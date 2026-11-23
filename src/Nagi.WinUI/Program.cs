using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Settings;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Implementations;
using WinRT;

namespace Nagi.WinUI;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        using var singleInstanceManager = new SingleInstanceManager();
        var filePath = TryGetFilePathFromArgs(args);
        var command = JumpListService.Parse(args);

        if (!singleInstanceManager.TryAcquire())
        {
            using var activationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            singleInstanceManager
                .SendActivationAsync(filePath, command, activationTimeout.Token)
                .GetAwaiter()
                .GetResult();
            return 0;
        }

        LanguageBootstrapper.Bootstrap();

        XamlOptionalChanges.EnableChange(XamlChangeId.DefaultStyleOptimizations);
        XamlOptionalChanges.EnableChange(XamlChangeId.DeferContextFlyoutInit);
        XamlOptionalChanges.EnableChange(XamlChangeId.IconNoGridOptimization);
        XamlOptionalChanges.EnableChange(XamlChangeId.OptimizeApplyStyles);

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(singleInstanceManager, filePath, command);
        });

        return 0;
    }

    private static string? TryGetFilePathFromArgs(string[] args)
    {
        var path = args.LastOrDefault();
        return File.Exists(path) || Directory.Exists(path) ? path : null;
    }
}
