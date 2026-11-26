using Fumilume.Services;

namespace Fumilume.Tests;

/// <summary>
/// テスト用の <see cref="IShellFileService"/>。一時フォルダ上で実際にファイルを動かし、呼び出しを記録する。
/// ごみ箱は <see cref="TrashFolder"/> への退避で代える。失敗・取り消しは <see cref="Inject"/> で 1 回ぶんだけ差し込める。
/// クリップボードはプロセス内に持つ。
/// </summary>
public sealed class FakeShellFileService(string trashFolder) : IShellFileService
{
    public const string RenameOperation = "Rename";
    public const string TrashOperation = "MoveToTrash";
    public const string TransferOperation = "Transfer";
    public const string CreateFileOperation = "CreateFile";
    public const string CreateFolderOperation = "CreateFolder";
    public const string RevealOperation = "Reveal";
    public const string OpenTerminalOperation = "OpenTerminal";
    public const string OpenFolderOperation = "OpenFolder";
    public const string ShowPropertiesOperation = "ShowProperties";
    public const string SetClipboardOperation = "SetFileClipboard";

    private readonly Dictionary<string, (ShellOperationOutcome Outcome, string? Message)> _injected = new();
    private FileClipboardContent? _clipboard;
    private long _token;

    /// <summary>削除（ごみ箱）の退避先。</summary>
    public string TrashFolder { get; } = trashFolder;

    public bool CanShowProperties { get; set; }

    /// <summary>呼び出しの記録（操作名, 引数の要約）。</summary>
    public List<(string Operation, string Detail)> Calls { get; } = [];

    /// <summary>ごみ箱へ移した元のパス。</summary>
    public List<string> TrashedPaths { get; } = [];

    public FileClipboardContent? Clipboard => _clipboard;

    /// <summary>次の <paramref name="operation"/>（定数）を、何もせずこの結果で終わらせる（1 回で解除）。</summary>
    public void Inject(string operation, ShellOperationOutcome outcome, string? message = null)
        => _injected[operation] = (outcome, message ?? "注入された失敗");

    public IEnumerable<string> CallsOf(string operation)
        => Calls.Where(c => c.Operation == operation).Select(c => c.Detail);

    private ShellOperationResult? TakeInjected(string operation, string detail)
    {
        Calls.Add((operation, detail));
        if (!_injected.Remove(operation, out var injected))
        {
            return null;
        }

        return injected.Outcome == ShellOperationOutcome.Cancelled
            ? ShellOperationResult.Cancelled()
            : ShellOperationResult.Failed(injected.Message ?? string.Empty);
    }

    public Task<ShellOperationResult> RenameAsync(string path, string newName)
    {
        if (TakeInjected(RenameOperation, $"{path}|{newName}") is { } injected)
        {
            return Task.FromResult(injected);
        }

        var parent = Path.GetDirectoryName(path)!;
        var target = Path.Combine(parent, newName);
        var caseOnly = FileNameRules.ArePathsEqual(path, target);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
        {
            return Task.FromResult(ShellOperationResult.Failed("変更先が既にあります。"));
        }

        try
        {
            MovePath(path, target, caseOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ShellOperationResult.Failed(ex.Message));
        }

        return Task.FromResult(ShellOperationResult.Success([new ShellTransfer(path, target)]));
    }

    public Task<ShellOperationResult> MoveToTrashAsync(IReadOnlyList<string> paths)
    {
        if (TakeInjected(TrashOperation, string.Join("|", paths)) is { } injected)
        {
            return Task.FromResult(injected);
        }

        Directory.CreateDirectory(TrashFolder);
        var completed = new List<ShellTransfer>();
        foreach (var path in paths)
        {
            try
            {
                MovePath(path, UniqueName(TrashFolder, Path.GetFileName(path), Directory.Exists(path)), false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(ShellOperationResult.Failed(ex.Message, completed));
            }

            TrashedPaths.Add(path);
            completed.Add(new ShellTransfer(path, string.Empty));
        }

        return Task.FromResult(ShellOperationResult.Success(completed));
    }

    public Task<ShellOperationResult> TransferAsync(
        IReadOnlyList<string> sources, string destinationFolder, bool move, bool renameOnCollision)
    {
        var detail = $"{string.Join("|", sources)}=>{destinationFolder};move={move};rename={renameOnCollision}";
        if (TakeInjected(TransferOperation, detail) is { } injected)
        {
            return Task.FromResult(injected);
        }

        var completed = new List<ShellTransfer>();
        var skipped = 0;
        foreach (var source in sources)
        {
            var isDirectory = Directory.Exists(source);
            if (isDirectory && FileNameRules.IsSameOrDescendant(destinationFolder, source))
            {
                return Task.FromResult(ShellOperationResult.Failed("移動先が移動元の配下です。", completed));
            }

            var name = Path.GetFileName(source);
            var sameFolder = FileNameRules.ArePathsEqual(Path.GetDirectoryName(source)!, destinationFolder);
            var target = Path.Combine(destinationFolder, name);
            var collides = File.Exists(target) || Directory.Exists(target);
            if (move && sameFolder)
            {
                skipped++;
                continue;
            }

            if (collides && !renameOnCollision)
            {
                skipped++;
                continue;
            }

            if (collides)
            {
                target = UniqueName(destinationFolder, name, isDirectory);
            }

            try
            {
                if (move)
                {
                    MovePath(source, target, false);
                }
                else
                {
                    Copy(source, target, isDirectory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(ShellOperationResult.Failed(ex.Message, completed));
            }

            completed.Add(new ShellTransfer(source, target));
        }

        return Task.FromResult(skipped == 0
            ? ShellOperationResult.Success(completed)
            : ShellOperationResult.Failed($"{skipped} 件は同名のため飛ばしました。", completed));
    }

    public Task<ShellOperationResult> CreateFileAsync(string path)
    {
        if (TakeInjected(CreateFileOperation, path) is { } injected)
        {
            return Task.FromResult(injected);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ShellOperationResult.Failed(ex.Message));
        }

        return Task.FromResult(ShellOperationResult.Success([new ShellTransfer(path, path)]));
    }

    public Task<ShellOperationResult> CreateFolderAsync(string path)
    {
        if (TakeInjected(CreateFolderOperation, path) is { } injected)
        {
            return Task.FromResult(injected);
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            return Task.FromResult(ShellOperationResult.Failed("同名のファイルまたはフォルダが既にあります。"));
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ShellOperationResult.Failed(ex.Message));
        }

        return Task.FromResult(ShellOperationResult.Success([new ShellTransfer(path, path)]));
    }

    public Task<bool> SetFileClipboardAsync(IReadOnlyList<string> paths, bool isCut)
    {
        Calls.Add((SetClipboardOperation, $"{string.Join("|", paths)};cut={isCut}"));
        if (_injected.Remove(SetClipboardOperation))
        {
            return Task.FromResult(false);
        }

        _clipboard = new FileClipboardContent([.. paths], isCut, ++_token);
        return Task.FromResult(true);
    }

    public Task<FileClipboardContent?> GetFileClipboardAsync() => Task.FromResult(_clipboard);

    public Task<bool> HasFileClipboardAsync() => Task.FromResult(_clipboard is not null);

    public Task<bool> ClearFileClipboardIfUnchangedAsync(long token)
    {
        if (_clipboard?.Token != token)
        {
            return Task.FromResult(false);
        }

        _clipboard = null;
        return Task.FromResult(true);
    }

    public ShellOperationResult Reveal(string path)
        => TakeInjected(RevealOperation, path) ?? ShellOperationResult.Success();

    public ShellOperationResult OpenFolder(string folder)
        => TakeInjected(OpenFolderOperation, folder) ?? ShellOperationResult.Success();

    public ShellOperationResult OpenTerminal(string folder)
        => TakeInjected(OpenTerminalOperation, folder) ?? ShellOperationResult.Success();

    public ShellOperationResult ShowProperties(string path)
        => TakeInjected(ShowPropertiesOperation, path) ?? ShellOperationResult.Success();

    private static string UniqueName(string folder, string name, bool isDirectory)
    {
        var candidate = Path.Combine(folder, name);
        for (var number = 1; File.Exists(candidate) || Directory.Exists(candidate); number++)
        {
            candidate = Path.Combine(folder, FileNameRules.GetCopyName(name, number, isDirectory));
        }

        return candidate;
    }

    private static void MovePath(string source, string target, bool caseOnly)
    {
        if (caseOnly)
        {
            // 大文字小文字だけの変更は、一度別名を経由する。
            var temp = source + "." + Guid.NewGuid().ToString("N");
            MoveCore(source, temp);
            MoveCore(temp, target);
            return;
        }

        MoveCore(source, target);
    }

    private static void MoveCore(string source, string target)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target);
        }
    }

    private static void Copy(string source, string target, bool isDirectory)
    {
        if (!isDirectory)
        {
            File.Copy(source, target);
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)), true);
        }
    }
}
