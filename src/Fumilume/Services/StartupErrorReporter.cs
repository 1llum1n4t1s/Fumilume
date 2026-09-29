using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Fumilume.Services;

/// <summary>Avalonia の起動前に失敗した場合に OS の標準ダイアログで通知する。</summary>
internal static class StartupErrorReporter
{
    private const uint ErrorIcon = 0x00000010;

    public static void Show(string message)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = MessageBox(IntPtr.Zero, message, "Fumilume", ErrorIcon);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var startInfo = new ProcessStartInfo("/usr/bin/osascript")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var escaped = message.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .Replace("\r", " ", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal);
                startInfo.ArgumentList.Add("-e");
                startInfo.ArgumentList.Add($"display alert \"Fumilume\" message \"{escaped}\" as critical");
                using var process = Process.Start(startInfo);
                if (process is not null)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            {
                // 標準ダイアログを起動できない場合も失敗内容を捨てない。
            }
        }

        Console.Error.WriteLine(message);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr windowHandle, string text, string caption, uint type);
}
