using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Fumilume.Services;
using Fumilume.Views;

namespace Fumilume;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 設定はここで一度だけ読み、テーマへ反映してからウィンドウを作る。
            // ウィンドウ生成後にテーマを変えると、初回描画で既定テーマが一瞬見える。
            var settings = SettingsService.Load();
            ThemeService.Initialize(this, settings);
            var mainWindow = new MainWindow(settings);
            desktop.MainWindow = mainWindow;
            if (OperatingSystem.IsMacOS())
            {
                // Cocoa の終了は Main の finally より先にプロセスを終えることがある。
                // セッション保存後の lifetime 終了通知でログを確実に書き出す。
                desktop.Exit += (_, _) => Program.CompleteShutdown();
            }
            Program.SingleInstance?.SetArgumentsHandler(arguments =>
                Dispatcher.UIThread.Post(() => mainWindow.OpenForwardedArguments(arguments)));

            if (OperatingSystem.IsMacOS() && this.TryGetFeature<IActivatableLifetime>() is { } lifetime)
            {
                // Finder からの「このアプリで開く」は起動引数ではなく activation として届く。
                lifetime.Activated += (_, args) =>
                {
                    if (args is not FileActivatedEventArgs fileArgs)
                    {
                        return;
                    }

                    var paths = fileArgs.Files
                        .Select(file => file.TryGetLocalPath())
                        .OfType<string>()
                        .ToArray();
                    if (paths.Length > 0)
                    {
                        Dispatcher.UIThread.Post(() => mainWindow.OpenForwardedArguments(paths));
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
