namespace Fumilume.Services;

/// <summary>
/// シェルを使わずにファイル・フォルダをコピー・移動する（macOS 実装が使う。OS 非依存なので Windows でもテストできる）。
/// 衝突は上書きしない。<c>renameOnCollision</c> が true なら「名前 のコピー」「名前 のコピー 2」の形で自動改名し、
/// false なら飛ばして件数を <see cref="ShellOperationResult.ErrorMessage"/> に入れる。済んだ分は常に Completed に入れる。
/// </summary>
public static class ManagedFileTransfer
{
    public static ShellOperationResult Transfer(
        IReadOnlyList<string> sources, string destinationFolder, bool move, bool renameOnCollision)
        => Transfer(sources, destinationFolder, move, renameOnCollision, null);

    /// <summary>テスト用の差し込み口。別ボリュームの移動（コピーしてから元を消す経路）を強制し、元の削除を差し替える。</summary>
    internal sealed record Hooks(bool ForceCopyThenDelete, Action<string> DeleteSource);

    internal static ShellOperationResult Transfer(
        IReadOnlyList<string> sources, string destinationFolder, bool move, bool renameOnCollision, Hooks? hooks)
    {
        if (sources.Count == 0)
        {
            return ShellOperationResult.Success();
        }

        var destination = TrimEnd(Path.GetFullPath(destinationFolder));
        if (!Directory.Exists(destination))
        {
            return ShellOperationResult.Failed($"転送先のフォルダが見つかりません: {destination}");
        }

        foreach (var source in sources)
        {
            var full = TrimEnd(Path.GetFullPath(source));
            if (IsRealDirectory(full) && FileNameRules.IsSameOrDescendant(destination, full))
            {
                return ShellOperationResult.Failed($"「{Path.GetFileName(full)}」を自分自身またはその中へは{(move ? "移動" : "コピー")}できません。");
            }
        }

        var completed = new List<ShellTransfer>();
        var skipped = 0;
        var failed = new List<string>();
        foreach (var source in sources)
        {
            var full = TrimEnd(Path.GetFullPath(source));
            try
            {
                switch (TransferOne(full, destination, move, renameOnCollision, hooks, out var target, out var warning))
                {
                    case Step.Done:
                        completed.Add(new ShellTransfer(full, target));
                        if (warning is not null)
                        {
                            failed.Add(warning);
                        }

                        break;
                    case Step.Skipped:
                        skipped++;
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failed.Add($"「{Path.GetFileName(full)}」: {ex.Message}");
            }
        }

        var message = BuildMessage(skipped, failed);
        return message is null
            ? ShellOperationResult.Success(completed)
            : ShellOperationResult.Failed(message, completed);
    }

    private enum Step
    {
        Done,
        Skipped,
    }

    private static string? BuildMessage(int skipped, List<string> failed)
    {
        var parts = new List<string>();
        if (skipped > 0)
        {
            parts.Add($"{skipped} 件は転送先に同名の項目があるため、上書きせずに飛ばしました。");
        }

        if (failed.Count > 0)
        {
            parts.Add($"{failed.Count} 件が失敗しました。{failed[0]}");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static Step TransferOne(
        string source, string destination, bool move, bool renameOnCollision, Hooks? hooks,
        out string target, out string? warning)
    {
        warning = null;
        var isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source) && !IsLink(source))
        {
            throw new FileNotFoundException("元の項目が見つかりません。", source);
        }

        var leaf = Path.GetFileName(source);
        var sameFolder = FileNameRules.ArePathsEqual(Path.GetDirectoryName(source) ?? string.Empty, destination);
        if (move && sameFolder)
        {
            target = source;
            return Step.Done;
        }

        var found = ResolveTarget(destination, leaf, isDirectory, renameOnCollision);
        if (found is null)
        {
            target = string.Empty;
            return Step.Skipped;
        }

        target = found;
        if (move)
        {
            warning = MoveEntry(source, target, isDirectory, hooks);
        }
        else
        {
            CopyEntry(source, target);
        }

        return Step.Done;
    }

    private static string? ResolveTarget(string destination, string leaf, bool isDirectory, bool renameOnCollision)
    {
        var plain = Path.Combine(destination, leaf);
        if (!Exists(plain))
        {
            return plain;
        }

        if (!renameOnCollision)
        {
            return null;
        }

        for (var number = 1; ; number++)
        {
            var candidate = Path.Combine(destination, FileNameRules.GetCopyName(leaf, number, isDirectory));
            if (!Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>戻り値は、コピーは済んだが元を消せなかったときの警告（それ以外は null）。</summary>
    private static string? MoveEntry(string source, string target, bool isDirectory, Hooks? hooks)
    {
        if (!isDirectory || IsLink(source))
        {
            File.Move(source, target, overwrite: false);
            return null;
        }

        if (hooks is { ForceCopyThenDelete: true })
        {
            return CopyThenDelete(source, target, hooks);
        }

        try
        {
            Directory.Move(source, target);
            return null;
        }
        catch (IOException) when (Directory.Exists(source) && !Exists(target))
        {
            // 別ボリュームなど。全部コピーできてから元を消す。コピーが途中で失敗したら、作りかけを消して元を残す。
            return CopyThenDelete(source, target, hooks);
        }
    }

    private static string? CopyThenDelete(string source, string target, Hooks? hooks)
    {
        try
        {
            CopyEntry(source, target);
        }
        catch
        {
            TryDeleteTree(target);
            throw;
        }

        // コピーは全部済んでいるので、元の削除に失敗しても例外にせず、移動先を済んだ分として返す（データは消さない）。
        try
        {
            if (hooks is not null)
            {
                hooks.DeleteSource(source);
            }
            else
            {
                Directory.Delete(source, recursive: true);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"「{Path.GetFileName(source)}」: コピーは完了しましたが、元のフォルダを削除できませんでした。{ex.Message}";
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 作りかけの後始末に失敗しても、元のエラーを優先して返す。
        }
    }

    private static void CopyEntry(string source, string target)
    {
        try
        {
            CopyEntryCore(source, target);
        }
        catch
        {
            if (Directory.Exists(target) && !IsLink(target))
            {
                TryDeleteTree(target);
            }

            throw;
        }
    }

    private static void CopyEntryCore(string source, string target)
    {
        var info = GetInfo(source);
        if (info.LinkTarget is { } link)
        {
            if (info is DirectoryInfo)
            {
                Directory.CreateSymbolicLink(target, link);
            }
            else
            {
                File.CreateSymbolicLink(target, link);
            }

            return;
        }

        if (info is FileInfo)
        {
            File.Copy(source, target, overwrite: false);
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var child in Directory.EnumerateFileSystemEntries(source))
        {
            CopyEntryCore(child, Path.Combine(target, Path.GetFileName(child)));
        }
    }

    private static FileSystemInfo GetInfo(string path)
        => Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

    private static bool IsLink(string path) => GetInfo(path).LinkTarget is not null;

    private static bool IsRealDirectory(string path) => Directory.Exists(path) && !IsLink(path);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || IsLink(path);

    private static string TrimEnd(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }
}
