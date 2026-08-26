using Avalonia.Controls;
using Velopack;
using Velopack.Sources;

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
            if (!manager.IsInstalled)
            {
                if (manually && owner is not null)
                {
                    await ShowMessageAsync(owner, "開発実行中は更新を確認できません。");
                }

                return;
            }

            using var timeout = manually ? null : new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await VelopackUpdateDialog.UpdateDialogWindow.ShowAsync(
                owner,
                manager,
                new VelopackUpdateDialog.UpdateDialogOptions(),
                manualCheck: manually,
                timeout?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) when (!manually)
        {
            // 起動時の確認は通信環境が悪くても編集を妨げない。
        }
        catch (Exception ex)
        {
            if (manually && owner is not null)
            {
                await ShowMessageAsync(owner, $"更新を確認できませんでした。\n{ex.Message}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
    }

    private static async Task ShowMessageAsync(Window owner, string message)
    {
        var close = new Button { Content = "閉じる", IsDefault = true, IsCancel = true, MinWidth = 88 };
        var dialog = new Window
        {
            Title = "Fumilume",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 20,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    close,
                },
            },
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }
}
