using System.Runtime.InteropServices;

namespace Fumilume.Services;

/// <summary>
/// Windows のシェル操作。ファイル操作は IFileOperation（ごみ箱・進捗・名前衝突の確認がエクスプローラーと同じ）、
/// クリップボードは CF_HDROP、表示・ターミナル・プロパティはシェルの API を使う。
/// 例外は投げず、失敗は <see cref="ShellOperationOutcome.Failed"/> で返す。
/// テスト用の抑止モードでは削除を拒否する（ごみ箱が無効な環境の完全削除の確認は抑止できないため）。
/// </summary>
public sealed class WindowsShellFileService : IShellFileService
{
    private readonly Func<nint>? _ownerWindow;
    private readonly bool _suppressShellUi;

    /// <param name="ownerWindow">進捗・確認ダイアログの親にするウィンドウハンドルを返す関数（UI スレッドで呼ばれる）。</param>
    public WindowsShellFileService(Func<nint>? ownerWindow = null) => _ownerWindow = ownerWindow;

    /// <summary>テスト用。<paramref name="suppressShellUi"/> が true のとき、IFileOperation の進捗・エラー・確認 UI を出さない
    /// 抑止モードでは削除を拒否する（完全削除の確認は抑止できないため）。本番は上の公開コンストラクターを使う。</summary>
    internal WindowsShellFileService(Func<nint>? ownerWindow, bool suppressShellUi)
    {
        _ownerWindow = ownerWindow;
        _suppressShellUi = suppressShellUi;
    }

    public bool CanShowProperties => true;

    public async Task<ShellOperationResult> RenameAsync(string path, string newName)
    {
        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            var parent = Path.GetDirectoryName(trimmed);
            if (parent is null || !(File.Exists(trimmed) || Directory.Exists(trimmed)))
            {
                return ShellOperationResult.Failed($"「{path}」が見つかりません。");
            }

            var leaf = Path.GetFileName(trimmed);
            if (string.Equals(leaf, newName, StringComparison.Ordinal))
            {
                return ShellOperationResult.Success([new ShellTransfer(path, path)]);
            }

            var target = Path.Combine(parent, newName);
            var caseOnly = string.Equals(leaf, newName, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            {
                return ShellOperationResult.Failed(FileNameRules.ExistsMessage(newName));
            }

            var sink = new WindowsFileOperationSink();
            var result = await WindowsFileOperation.RenameAsync(trimmed, newName, sink, Owner(), _suppressShellUi);
            IReadOnlyList<ShellTransfer> completed = sink.Renamed;
            if (result.Outcome == ShellOperationOutcome.Success && completed.Count == 0)
            {
                // 実名を通知から得られなかったときは、要求した名前で代える（同じフォルダ内の改名なので確定している）。
                completed = [new ShellTransfer(path, target)];
            }

            return ToResult(result, completed);
        }
        catch (Exception ex)
        {
            return ShellOperationResult.Failed(ex.Message);
        }
    }

    public async Task<ShellOperationResult> MoveToTrashAsync(IReadOnlyList<string> paths)
    {
        try
        {
            if (paths.Count == 0)
            {
                return ShellOperationResult.Success();
            }

            if (_suppressShellUi)
            {
                return ShellOperationResult.Failed("テスト用の抑止モードでは削除しない。");
            }

            var owner = Owner();
            var completed = new List<ShellTransfer>();
            var sink = new WindowsFileOperationSink();
            var result = await WindowsFileOperation.DeleteAsync(paths, false, sink, owner);
            CollectDeleted(sink, completed);

            // 非対話の 1 回目が失敗で止まったときは、残りをシェル標準のエラー・権限確認付きで続ける。
            // 取り消し（利用者が確認を断った）では重ねない。
            if (result.IsFailed)
            {
                var remaining = paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
                if (remaining.Count > 0)
                {
                    var retrySink = new WindowsFileOperationSink();
                    result = await WindowsFileOperation.DeleteAsync(remaining, true, retrySink, owner);
                    CollectDeleted(retrySink, completed);
                }
            }

            return ToResult(result, completed, paths.Count);
        }
        catch (Exception ex)
        {
            return ShellOperationResult.Failed(ex.Message);
        }
    }

    public async Task<ShellOperationResult> TransferAsync(
        IReadOnlyList<string> sources, string destinationFolder, bool move, bool renameOnCollision)
    {
        try
        {
            if (sources.Count == 0)
            {
                return ShellOperationResult.Success();
            }

            if (!Directory.Exists(destinationFolder))
            {
                return ShellOperationResult.Failed($"「{destinationFolder}」が見つかりません。");
            }

            if (sources.Any(source => Directory.Exists(source) && FileNameRules.IsSameOrDescendant(destinationFolder, source)))
            {
                return ShellOperationResult.Failed("フォルダを自分自身またはその中へ移動・コピーすることはできません。");
            }

            var sink = new WindowsFileOperationSink();
            var result = await WindowsFileOperation.TransferAsync(
                sources, destinationFolder, move, renameOnCollision, sink, Owner(), _suppressShellUi);
            return ToResult(result, move ? sink.Moved : sink.Copied, sources.Count);
        }
        catch (Exception ex)
        {
            return ShellOperationResult.Failed(ex.Message);
        }
    }

    public Task<ShellOperationResult> CreateFileAsync(string path)
        => Task.Run(() => Create(path, isDirectory: false));

    public Task<ShellOperationResult> CreateFolderAsync(string path)
        => Task.Run(() => Create(path, isDirectory: true));

    public Task<bool> SetFileClipboardAsync(IReadOnlyList<string> paths, bool isCut)
    {
        try
        {
            return Task.FromResult(WindowsFileClipboard.SetFiles(paths, isCut));
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<FileClipboardContent?> GetFileClipboardAsync()
    {
        try
        {
            return Task.FromResult(WindowsFileClipboard.TryGetFiles());
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            return Task.FromResult<FileClipboardContent?>(null);
        }
    }

    public Task<bool> HasFileClipboardAsync() => Task.FromResult(WindowsFileClipboard.HasFiles());

    public Task<bool> ClearFileClipboardIfUnchangedAsync(long token)
        => Task.FromResult(WindowsFileClipboard.ClearIfUnchanged(token));

    public ShellOperationResult Reveal(string path) => WindowsShellLauncher.Reveal(path);

    /// <summary>フォルダの中身をエクスプローラーで開く（<see cref="IShellFileService"/> には無い Windows 固有の入口）。</summary>
    public ShellOperationResult OpenFolder(string folder) => WindowsShellLauncher.OpenFolder(folder);

    public ShellOperationResult OpenTerminal(string folder) => WindowsShellLauncher.OpenTerminal(folder);

    public ShellOperationResult ShowProperties(string path) => WindowsShellLauncher.ShowProperties(path, Owner());

    private nint Owner() => _ownerWindow?.Invoke() ?? 0;

    private static void CollectDeleted(WindowsFileOperationSink sink, List<ShellTransfer> completed)
    {
        // 削除の通知が成功でも、完全に消えたことを確かめてから済んだ分に数える。
        completed.AddRange(sink.Deleted.Where(item => !File.Exists(item.Source) && !Directory.Exists(item.Source)));
    }

    /// <summary>結果を契約の形にする。<paramref name="requested"/> は要求した項目数で、
    /// 成功扱いなのに済んだ数が足りないとき（利用者が項目を飛ばした）は取り消しとして返す。</summary>
    private static ShellOperationResult ToResult(
        WindowsFileOperationResult result, IReadOnlyList<ShellTransfer> completed, int? requested = null)
    {
        var snapshot = completed.ToArray();
        return result.Outcome switch
        {
            ShellOperationOutcome.Cancelled => ShellOperationResult.Cancelled(snapshot),
            ShellOperationOutcome.Failed => ShellOperationResult.Failed(
                WindowsFileOperation.DescribeError(result.ErrorCode), snapshot),
            _ when requested is { } count && snapshot.Length < count => ShellOperationResult.Cancelled(snapshot),
            _ => ShellOperationResult.Success(snapshot),
        };
    }

    private static ShellOperationResult Create(string path, bool isDirectory)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is null || !Directory.Exists(parent))
            {
                return ShellOperationResult.Failed($"「{parent}」が見つかりません。");
            }

            if (File.Exists(path) || Directory.Exists(path))
            {
                return ShellOperationResult.Failed(FileNameRules.ExistsMessage(Path.GetFileName(path)));
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
            }

            return ShellOperationResult.Success([new ShellTransfer(path, path)]);
        }
        catch (Exception ex)
        {
            return ShellOperationResult.Failed(ex.Message);
        }
    }
}
