using Avalonia.Controls;
using Fumilume.Views;
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

            if (owner is null)
            {
                return;
            }

            using var timeout = manually ? null : new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var update = await manager.CheckForUpdatesAsync()
                .WaitAsync(timeout?.Token ?? CancellationToken.None);
            if (update is null)
            {
                if (manually)
                {
                    await ShowMessageAsync(owner, "Fumilumeは最新です。");
                }

                return;
            }

            await ShowUpdateAsync(owner, manager, update);
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

    private static async Task ShowUpdateAsync(Window owner, UpdateManager manager, UpdateInfo update)
    {
        var status = new TextBlock
        {
            Text = $"Fumilume {update.TargetFullRelease.Version} を利用できます。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 14,
        };
        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            IsVisible = false,
        };
        var updateNow = new Button { Content = "更新して再起動", IsDefault = true, MinWidth = 128 };
        var later = new Button { Content = "後で", IsCancel = true, MinWidth = 88 };
        var content = new StackPanel
        {
            Spacing = 14,
            Children = { status, progress },
        };
        var dialog = new AppDialogWindow("更新の確認", content, updateNow, later)
        {
            Width = 460,
        };
        using var cancellation = new CancellationTokenSource();
        Task? updateTask = null;

        updateNow.Click += (_, _) => updateTask ??= DownloadAndApplyAsync();
        later.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => cancellation.Cancel();

        await dialog.ShowDialog(owner);
        if (updateTask is not null)
        {
            await updateTask;
        }

        async Task DownloadAndApplyAsync()
        {
            updateNow.IsEnabled = false;
            later.IsEnabled = false;
            progress.IsVisible = true;
            status.Text = "更新をダウンロードしています…";

            try
            {
                IProgress<int> progressReporter = new Progress<int>(value => progress.Value = value);
                await manager.DownloadUpdatesAsync(update, progressReporter.Report, cancellation.Token);
                status.Text = "更新を適用して再起動します…";
                manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
            }
            catch (OperationCanceledException)
            {
                // ダイアログを閉じた場合は更新を中止する。
            }
            catch (Exception ex)
            {
                status.Text = $"更新を適用できませんでした。\n{ex.Message}";
                progress.IsVisible = false;
                updateNow.IsEnabled = true;
                later.IsEnabled = true;
                updateTask = null;
            }
        }
    }

    private static async Task ShowMessageAsync(Window owner, string message)
    {
        var close = new Button { Content = "閉じる", IsDefault = true, IsCancel = true, MinWidth = 88 };
        var dialog = new AppDialogWindow(
            "更新の確認",
            new TextBlock
            {
                Text = message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                FontSize = 14,
            },
            close)
        {
            Width = 440,
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }
}
