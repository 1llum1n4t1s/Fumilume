using Fumilume.Services;
using System.Runtime.Versioning;

namespace Fumilume.Tests;

/// <summary>IFileOperation を通す実ファイル操作の検証。テストではシェル UI（進捗・エラー・確認ダイアログ）を出さない。
/// このテストでは削除（ごみ箱が無効な環境では完全削除の確認が出る）・クリップボード書き込み・
/// ターミナル／エクスプローラー／プロパティの起動を呼ばない（ランチャーは起動引数の組み立てだけを検証する）。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Fumilume.Tests", "Shell-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsShellFileService _service = new(ownerWindow: null, suppressShellUi: true);

    public WindowsShellFileServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MakeFile(string relativePath, string content = "x")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Rename_ChangesNameAndReportsTransfer()
    {
        var source = MakeFile("a.txt", "body");

        var result = await _service.RenameAsync(source, "b.txt");

        var target = Path.Combine(_root, "b.txt");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(new ShellTransfer(source, target), Assert.Single(result.Completed));
        Assert.False(File.Exists(source));
        Assert.Equal("body", File.ReadAllText(target));
    }

    [Fact]
    public async Task Rename_CaseOnlyChange_IsAccepted()
    {
        var source = MakeFile("memo.txt");

        var result = await _service.RenameAsync(source, "MEMO.txt");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(Path.Combine(_root, "MEMO.txt"), Assert.Single(result.Completed).Destination);
        Assert.Contains("MEMO.txt", Directory.EnumerateFiles(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Rename_FolderToExistingName_FailsWithoutChanges()
    {
        var source = MakeFile(@"dir\inner.txt");
        MakeFile("taken\\other.txt");

        var result = await _service.RenameAsync(Path.GetDirectoryName(source)!, "taken");

        Assert.True(result.IsFailed);
        Assert.Empty(result.Completed);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Transfer_CopyKeepsSourceAndReportsDestination()
    {
        var source = MakeFile("a.txt");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;

        var result = await _service.TransferAsync([source], folder, move: false, renameOnCollision: true);

        var destination = Path.Combine(folder, "a.txt");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(new ShellTransfer(source, destination), Assert.Single(result.Completed));
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task Transfer_MoveRemovesSource()
    {
        var source = MakeFile(@"src\a.txt");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;

        var result = await _service.TransferAsync([source], folder, move: true, renameOnCollision: true);

        var destination = Path.Combine(folder, "a.txt");
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(destination, Assert.Single(result.Completed).Destination);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task Transfer_CopyIntoSameFolderRenamesAutomatically()
    {
        var source = MakeFile("a.txt", "body");

        var result = await _service.TransferAsync([source], _root, move: false, renameOnCollision: true);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var transfer = Assert.Single(result.Completed);
        Assert.Equal(source, transfer.Source);
        Assert.NotEqual(source, transfer.Destination, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_root, Path.GetDirectoryName(transfer.Destination));
        Assert.Equal("body", File.ReadAllText(transfer.Destination));
        Assert.Equal("body", File.ReadAllText(source));
    }

    [Fact]
    public async Task Transfer_FolderIntoItsOwnSubfolder_Fails()
    {
        var inner = MakeFile(@"dir\sub\a.txt");
        var folder = Path.Combine(_root, "dir");

        var result = await _service.TransferAsync([folder], Path.GetDirectoryName(inner)!, move: true, renameOnCollision: true);

        Assert.True(result.IsFailed);
        Assert.Empty(result.Completed);
        Assert.True(File.Exists(inner));
    }

    [Fact]
    public async Task Create_NewFileAndFolderSucceed_ExistingFails()
    {
        var file = Path.Combine(_root, "new.txt");
        var folder = Path.Combine(_root, "newdir");

        var created = await _service.CreateFileAsync(file);
        var createdFolder = await _service.CreateFolderAsync(folder);
        var duplicate = await _service.CreateFileAsync(file);
        var duplicateFolder = await _service.CreateFolderAsync(file);

        Assert.Equal(new ShellTransfer(file, file), Assert.Single(created.Completed));
        Assert.Equal(new ShellTransfer(folder, folder), Assert.Single(createdFolder.Completed));
        Assert.True(File.Exists(file) && Directory.Exists(folder));
        Assert.True(duplicate.IsFailed);
        Assert.True(duplicateFolder.IsFailed);
        Assert.Equal(0, new FileInfo(file).Length);
    }

    [Theory]
    [InlineData(@"C:\work\a;calc")]
    public void Launcher_PathWithSemicolon_IsNotGivenToWindowsTerminal(string folder)
        => Assert.False(WindowsShellLauncher.IsSafeForWindowsTerminal(folder));

    [Fact]
    public void Launcher_LocalFolder_UsesCmdWithWorkingDirectoryOnly()
    {
        var folder = @"C:\work\a&calc ^ %PATH% ’x";

        var spec = WindowsShellLauncher.BuildFallbackTerminalSpec(@"C:\Windows\System32", folder);

        Assert.EndsWith("cmd.exe", spec.Executable);
        Assert.Empty(spec.Arguments);
        Assert.Equal(folder, spec.WorkingDirectory);
        Assert.Null(spec.Environment);
    }

    [Fact]
    public void Launcher_UncFolder_PassesThePathOnlyThroughAnEnvironmentVariable()
    {
        var folder = @"\\server\share\it's ‘x’ ’ ‚ ‛;calc";

        var spec = WindowsShellLauncher.BuildFallbackTerminalSpec(@"C:\Windows\System32", folder);

        var encoded = spec.Arguments[^1];
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(WindowsShellLauncher.PowerShellScript, script);
        Assert.DoesNotContain("share", script);
        Assert.Equal(folder, spec.Environment![WindowsShellLauncher.TerminalDirectoryVariable]);
    }

    [Fact]
    public void Launcher_ExplorerPathWithComma_IsQuoted()
    {
        var reveal = WindowsShellLauncher.BuildRevealSpec(@"C:\Windows\explorer.exe", @"C:\a,b\c.txt");
        var plain = WindowsShellLauncher.BuildRevealSpec(@"C:\Windows\explorer.exe", @"C:\ab\c.txt");
        var open = WindowsShellLauncher.BuildOpenFolderSpec(@"C:\Windows\explorer.exe", @"C:\a,b");

        Assert.Equal("/select,\"C:\\a,b\\c.txt\"", reveal.RawArguments);
        Assert.Equal(["/select,", @"C:\ab\c.txt"], plain.Arguments);
        Assert.Equal("\"C:\\a,b\"", open.RawArguments);
    }
}
