using System.IO;
using System.Linq;
using System.Threading;
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

        if (!singleInstanceManager.TryAcquire())
        {
            using var activationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            singleInstanceManager
                .SendActivationAsync(TryGetFilePathFromArgs(args), JumpListService.Parse(args), activationTimeout.Token)
                .GetAwaiter()
                .GetResult();
            return 0;
        }

        LanguageBootstrapper.Bootstrap();

        XamlOptionalChanges.EnableChange(XamlChangeId.DefaultStyleOptimizations);
        XamlOptionalChanges.EnableChange(XamlChangeId.DeferContextFlyoutInit);
        XamlOptionalChanges.EnableChange(XamlChangeId.IconNoGridOptimization);
        XamlOptionalChanges.EnableChange(XamlChangeId.OptimizeApplyStyles);

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App(singleInstanceManager);
        });

        return 0;
    }

    internal static string? TryGetFilePathFromArgs(string[] args)
    {
        var path = args.LastOrDefault();
        return File.Exists(path) || Directory.Exists(path) ? path : null;
    }
}
