using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            Program.SingleInstance?.SetArgumentsHandler(arguments =>
                Dispatcher.UIThread.Post(() => mainWindow.OpenForwardedArguments(arguments)));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
