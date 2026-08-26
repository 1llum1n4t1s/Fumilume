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
        StartupArgs = args;
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => FileAssociationService.RefreshAssociatedFileTypes())
            .OnAfterUpdateFastCallback(_ => FileAssociationService.RefreshAssociatedFileTypes())
            .OnBeforeUninstallFastCallback(_ => CleanupBeforeUninstall())
            .Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static void CleanupBeforeUninstall(Func<bool>? disassociateAllFileTypes = null)
        => _ = (disassociateAllFileTypes ?? FileAssociationService.DisassociateAllFileTypes)();

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
