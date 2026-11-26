using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Fumilume.Services;

/// <summary>
/// エクスプローラーでの表示、ターミナル、プロパティ画面の起動。起動するのは固定パスの実行ファイルだけで、
/// 引数は <see cref="ProcessStartInfo.ArgumentList"/> で渡す（PATH 上の同名ファイルやシェルの解釈に頼らない）。
/// UI スレッドから呼ぶ前提。
/// </summary>
internal static partial class WindowsShellLauncher
{
    /// <summary>SHOP_FILEPATH: 名前がファイルシステムのパス。</summary>
    private const uint ShopFilePath = 0x2;

    public static ShellOperationResult Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return ShellOperationResult.Failed($"「{path}」が見つかりません。");
        }

        if (TrySelectInExplorer(path))
        {
            return ShellOperationResult.Success();
        }

        return Start(BuildRevealSpec(ExplorerPath(), path), "エクスプローラーを開けませんでした。");
    }

    /// <summary>フォルダの中身をエクスプローラーで開く。</summary>
    public static ShellOperationResult OpenFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return ShellOperationResult.Failed($"「{folder}」が見つかりません。");
        }

        return Start(BuildOpenFolderSpec(ExplorerPath(), folder), "エクスプローラーを開けませんでした。");
    }

    /// <summary>起動するプロセスの組み立て結果（実行ファイル・引数・開始位置・環境変数）。</summary>
    internal sealed record LaunchSpec(
        string Executable,
        IReadOnlyList<string> Arguments,
        string? WorkingDirectory,
        IReadOnlyDictionary<string, string>? Environment = null,
        string? RawArguments = null);

    /// <summary>explorer.exe はコマンドラインの <c>,</c> を区切りとして読むので、カンマを含むパスは引用符で囲んで渡す。</summary>
    internal static LaunchSpec BuildRevealSpec(string explorer, string path)
        => path.Contains(',')
            ? new LaunchSpec(explorer, [], null, RawArguments: "/select,\"" + Path.TrimEndingDirectorySeparator(path) + "\"")
            : new LaunchSpec(explorer, ["/select,", path], null);

    internal static LaunchSpec BuildOpenFolderSpec(string explorer, string folder)
        => folder.Contains(',')
            ? new LaunchSpec(explorer, [], null, RawArguments: "\"" + Path.TrimEndingDirectorySeparator(folder) + "\"")
            : new LaunchSpec(explorer, [folder], null);

    public static ShellOperationResult ShowProperties(string path, nint owner)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return ShellOperationResult.Failed($"「{path}」が見つかりません。");
        }

        return SHObjectProperties(owner, ShopFilePath, path, null)
            ? ShellOperationResult.Success()
            : ShellOperationResult.Failed("プロパティを開けませんでした。");
    }

    /// <summary>ターミナルを <paramref name="folder"/> で開く。Windows Terminal を優先し、無ければ cmd
    /// （UNC は cmd が開始位置にできないので PowerShell）で代える。</summary>
    public static ShellOperationResult OpenTerminal(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return ShellOperationResult.Failed($"「{folder}」が見つかりません。");
        }

        var wt = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(wt) && IsSafeForWindowsTerminal(folder)
            && Start(new LaunchSpec(wt, ["-d", folder], folder), string.Empty).IsSuccess)
        {
            return ShellOperationResult.Success();
        }

        return Start(BuildFallbackTerminalSpec(Environment.SystemDirectory, folder), "ターミナルを開けませんでした。");
    }

    /// <summary>wt.exe は引数内の <c>;</c> をサブコマンドの区切りとして解釈する（任意のコマンドを足せてしまう）ので、含むパスには使わない。</summary>
    internal static bool IsSafeForWindowsTerminal(string folder) => !folder.Contains(';');

    /// <summary>
    /// cmd（UNC は cmd が開始位置にできないので PowerShell）で開く。どちらもパスをコマンド文字列へ埋め込まない。
    /// cmd は <c>WorkingDirectory</c> で、PowerShell は環境変数 <c>FUMILUME_TERMINAL_DIR</c> を固定のスクリプトが読む形で渡す。
    /// </summary>
    internal static LaunchSpec BuildFallbackTerminalSpec(string system, string folder)
    {
        if (!folder.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new LaunchSpec(Path.Combine(system, "cmd.exe"), [], folder);
        }

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellScript));
        return new LaunchSpec(
            Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NoExit", "-EncodedCommand", encoded],
            system,
            new Dictionary<string, string> { [TerminalDirectoryVariable] = folder });
    }

    internal const string TerminalDirectoryVariable = "FUMILUME_TERMINAL_DIR";

    internal const string PowerShellScript = "Set-Location -LiteralPath $env:" + TerminalDirectoryVariable;

    private static string ExplorerPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    private static ShellOperationResult Start(LaunchSpec spec, string failureMessage)
    {
        try
        {
            if (!File.Exists(spec.Executable))
            {
                return ShellOperationResult.Failed(failureMessage);
            }

            var info = new ProcessStartInfo(spec.Executable) { UseShellExecute = false };
            if (spec.WorkingDirectory is not null)
            {
                info.WorkingDirectory = spec.WorkingDirectory;
            }

            if (spec.RawArguments is not null)
            {
                info.Arguments = spec.RawArguments;
            }

            foreach (var argument in spec.Arguments)
            {
                info.ArgumentList.Add(argument);
            }

            if (spec.Environment is not null)
            {
                foreach (var (name, value) in spec.Environment)
                {
                    info.Environment[name] = value;
                }
            }

            Process.Start(info)?.Dispose();
            return ShellOperationResult.Success();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return ShellOperationResult.Failed($"{failureMessage}（{ex.Message}）");
        }
    }

    private static bool TrySelectInExplorer(string path)
    {
        nint item = 0;
        try
        {
            if (SHParseDisplayName(path, 0, out item, 0, out _) < 0 || item == 0)
            {
                return false;
            }

            // 項目数 0 は「pidlFolder の項目そのものを親フォルダ内で選択する」指定。
            return SHOpenFolderAndSelectItems(item, 0, 0, 0) >= 0;
        }
        finally
        {
            if (item != 0)
            {
                CoTaskMemFree(item);
            }
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(
        string name, nint bindContext, out nint pidl, uint attributesIn, out uint attributesOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(nint pidlFolder, uint count, nint pidls, uint flags);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHObjectProperties(nint owner, uint objectType, string name, string? page);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint pointer);
}
