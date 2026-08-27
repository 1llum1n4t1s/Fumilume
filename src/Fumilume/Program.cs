using Avalonia;
using Fumilume.Services;
using Velopack;

namespace Fumilume;

internal static class Program
{
    public static string[] StartupArgs { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        AppLogger.Initialize();
        var log = AppLogger.For("Fumilume.Program");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            log.Fatal("未処理の例外でアプリを終了します。", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            log.Error("監視されていない Task 例外を検出しました。", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        try
        {
            StartupArgs = args;
            log.InfoFormat("Fumilume を起動します。引数: {0} 件", args.Length);
            VelopackApp.Build()
                .OnAfterInstallFastCallback(_ => FileAssociationService.RefreshAssociatedFileTypes())
                .OnAfterUpdateFastCallback(_ => FileAssociationService.RefreshAssociatedFileTypes())
                .OnBeforeUninstallFastCallback(_ => CleanupBeforeUninstall())
                .Run();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            log.Fatal("Fumilume の実行に失敗しました。", ex);
            throw;
        }
        finally
        {
            log.Info("Fumilume を終了します。");
            AppLogger.Shutdown();
        }
    }

    internal static void CleanupBeforeUninstall(Func<bool>? disassociateAllFileTypes = null)
        => _ = (disassociateAllFileTypes ?? FileAssociationService.DisassociateAllFileTypes)();

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
