using Avalonia.Controls;
using Avalonia.Media;
using Velopack;
using Velopack.Sources;
using VelopackUpdateDialog;

namespace Fumilume.Services;

public static class UpdateService
{
    public const string CanonicalUpdateBaseUrl = "https://fumilume.kagayoi.com";

    private static int _isChecking;

    public static async Task CheckAsync(Window? owner, bool manually)
    {
        if (Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var manager = new UpdateManager(new SimpleWebSource(CanonicalUpdateBaseUrl));
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
