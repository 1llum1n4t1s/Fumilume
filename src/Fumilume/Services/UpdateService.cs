using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Fumilume.Views;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using VelopackUpdateDialog;

namespace Fumilume.Services;

public static class UpdateService
{
    public const string CanonicalUpdateBaseUrl = "https://fumilume.kagayoi.com";

    private static int _isChecking;

    public static async Task CheckAsync(Window? owner, bool manually)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (owner is not MainWindow mainWindow)
            {
                throw new InvalidOperationException("更新を適用するには作業中のウィンドウが必要です。");
            }

            var currentLocator = VelopackLocator.Current;
            var process = new SessionSavingUpdateProcess(currentLocator.Process, mainWindow.PrepareForUpdateRestartAsync);
            var locator = VelopackLocator.CreateDefaultForPlatform(process, currentLocator.Log);
            var manager = new UpdateManager(new SimpleWebSource(CanonicalUpdateBaseUrl),
                new UpdateOptions { ExplicitChannel = OperatingSystem.IsMacOS() ? "osx-arm64" : null }, locator);
            using var timeout = manually ? null : new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var options = CreateOptions(owner);
            options.ErrorOccurred += LogUpdateError;

            await UpdateDialogWindow.ShowAsync(
                owner,
                manager,
                options,
                manualCheck: manually,
                cancellationToken: timeout?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) when (!manually)
        {
            // 起動時の確認は通信環境が悪くても編集を妨げない。
        }
        catch (Exception ex)
        {
            LogUpdateError(ex);
            if (manually && owner is not null)
            {
                await new EditorDialogService(owner).ShowErrorAsync(
                    "更新の確認",
                    $"更新を確認できませんでした。\n{ex.Message}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
    }

    private static UpdateDialogOptions CreateOptions(Window? owner)
    {
        IBrush? accentBrush = null;
        if (owner?.TryFindResource("AccentBrush", out var resource) == true)
        {
            accentBrush = resource as IBrush;
        }

        return new UpdateDialogOptions
        {
            Strings = FumilumeUpdateDialogStrings.Instance,
            ChromeMode = WindowChromeMode.Custom,
            ResizeMode = WindowResizeMode.Fixed,
            AccentBrush = accentBrush,
            AllowIgnoreVersion = false,
            SuppressUpToDateOnAutoCheck = true,
        };
    }

    private static void LogUpdateError(Exception exception)
        => AppLogger.For("Fumilume.UpdateService").Error("Fumilume の更新確認に失敗しました。", exception);

    // 更新 UI はワーカースレッドで適用する。updater 起動後には取り消せないため、
    // Exit ではなく StartProcess の手前で保存を確定させる。
    internal sealed class SessionSavingUpdateProcess(IProcessImpl process, Func<Task<bool>> prepareRestart) : IProcessImpl
    {
        private bool _prepared;

        public string GetCurrentProcessPath() => process.GetCurrentProcessPath();

        public uint GetCurrentProcessId() => process.GetCurrentProcessId();

        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
        {
            var arguments = args.ToArray();
            if (arguments.Contains("apply", StringComparer.Ordinal))
            {
                _prepared = false;
                if (Dispatcher.UIThread.CheckAccess())
                {
                    throw new InvalidOperationException("更新の適用は UI スレッドから実行できません。");
                }

                if (!Dispatcher.UIThread.InvokeAsync(prepareRestart).GetAwaiter().GetResult())
                {
                    throw new InvalidOperationException("保存が完了しなかったため、更新の適用を中止しました。");
                }

                process.StartProcess(exePath, arguments, workDir, showWindow);
                _prepared = true;
                return;
            }

            process.StartProcess(exePath, arguments, workDir, showWindow);
        }

        public void Exit(int exitCode)
        {
            if (!_prepared)
            {
                throw new InvalidOperationException("保存前に更新のため終了することはできません。");
            }

            process.Exit(exitCode);
        }
    }

    private sealed class FumilumeUpdateDialogStrings : IUpdateDialogStrings
    {
        public static readonly FumilumeUpdateDialogStrings Instance = new();

        public string Title => "Fumilume の更新";

        public string AvailableHeader => "新しいバージョンがあります";

        public string DownloadAndInstall => "更新して再起動";

        public string IgnoreThisVersion => "このバージョンを無視";

        public string UpToDateMessage => "Fumilume は最新です。";

        public string ErrorHeader => "更新を確認できませんでした";

        public string Close => "閉じる";

        public string CheckingMessage => "更新を確認しています…";
    }
}
