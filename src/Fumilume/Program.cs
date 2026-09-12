using Avalonia;
using Fumilume.Services;
using Velopack;

namespace Fumilume;

internal static class Program
{
    public static string[] StartupArgs { get; private set; } = [];

    internal static SingleInstanceCoordinator? SingleInstance { get; private set; }

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

            using var singleInstance = SingleInstanceCoordinator.Create();
            if (!singleInstance.IsPrimary)
            {
                ForwardArgumentsOrReport(singleInstance, args);
                return;
            }

            SingleInstance = singleInstance;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            log.Fatal("Fumilume の実行に失敗しました。", ex);
            throw;
        }
        finally
        {
            SingleInstance = null;
            log.Info("Fumilume を終了します。");
            AppLogger.Shutdown();
        }
    }

    internal static void CleanupBeforeUninstall(Func<bool>? disassociateAllFileTypes = null)
        => _ = (disassociateAllFileTypes ?? FileAssociationService.DisassociateAllFileTypes)();

    /// <summary>後続プロセスの作業フォルダーを基準に解決し、既存プロセスへ曖昧な相対パスを渡さない。</summary>
    internal static string[] NormalizeForwardedArguments(IEnumerable<string> arguments)
        => arguments.Select(argument =>
        {
            try
            {
                return Path.GetFullPath(argument);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return argument;
            }
        }).ToArray();

    /// <summary>後続起動の転送失敗を無言で捨てず、利用者へ再実行が必要だと知らせる。</summary>
    internal static void ForwardArgumentsOrReport(
        SingleInstanceCoordinator singleInstance,
        IEnumerable<string> arguments,
        Action<string>? reportFailure = null,
        int connectionTimeoutMilliseconds = 3000)
    {
        var forwarded = singleInstance.ForwardArgumentsAsync(
                NormalizeForwardedArguments(arguments),
                connectionTimeoutMilliseconds: connectionTimeoutMilliseconds)
            .GetAwaiter()
            .GetResult();
        if (forwarded)
        {
            return;
        }

        const string message = "既に起動している Fumilume へファイルを渡せませんでした。"
            + "少し待ってから、もう一度ファイルを開いてください。";
        (reportFailure ?? StartupErrorReporter.Show)(message);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .ConfigureFonts(fontManager =>
                fontManager.AddFontCollection(new FumilumeFontCollection()));
}
