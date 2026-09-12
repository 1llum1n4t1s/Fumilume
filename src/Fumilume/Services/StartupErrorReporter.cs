using System.Runtime.InteropServices;

namespace Fumilume.Services;

/// <summary>Avalonia の起動前に失敗した場合だけ、Windows の標準ダイアログで通知する。</summary>
internal static class StartupErrorReporter
{
    private const uint ErrorIcon = 0x00000010;

    public static void Show(string message)
        => _ = MessageBox(IntPtr.Zero, message, "Fumilume", ErrorIcon);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr windowHandle, string text, string caption, uint type);
}
