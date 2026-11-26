using System.Diagnostics;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Fumilume.Services;

/// <summary>
/// macOS 向けの <see cref="IShellFileService"/>。ごみ箱は NSFileManager、転送は <see cref="ManagedFileTransfer"/>、
/// クリップボードは Avalonia の <see cref="IClipboard"/>、表示・ターミナルは <c>/usr/bin/open</c>。プロパティは出さない。
/// </summary>
public sealed class MacShellFileService(Func<IClipboard?> getClipboard, Func<IStorageProvider?> getStorageProvider)
    : IShellFileService
{
    private const string OpenCommand = "/usr/bin/open";
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    private readonly MacFileClipboard _clipboard = new(getClipboard, getStorageProvider);

    public bool CanShowProperties => false;

    public Task<ShellOperationResult> RenameAsync(string path, string newName)
        => Task.Run(() => Rename(path, newName));

    public Task<ShellOperationResult> MoveToTrashAsync(IReadOnlyList<string> paths)
        => Task.Run(() => MoveToTrash(paths));

    public Task<ShellOperationResult> TransferAsync(
        IReadOnlyList<string> sources, string destinationFolder, bool move, bool renameOnCollision)
        => Task.Run(() => ManagedFileTransfer.Transfer(sources, destinationFolder, move, renameOnCollision));

    public Task<ShellOperationResult> CreateFileAsync(string path) => Task.Run(() => CreateFile(path));

    public Task<ShellOperationResult> CreateFolderAsync(string path) => Task.Run(() => CreateFolder(path));

    public Task<bool> SetFileClipboardAsync(IReadOnlyList<string> paths, bool isCut)
        => _clipboard.SetAsync(paths, isCut);

    public Task<FileClipboardContent?> GetFileClipboardAsync() => _clipboard.GetAsync();

    public Task<bool> HasFileClipboardAsync() => _clipboard.HasAsync();

    public Task<bool> ClearFileClipboardIfUnchangedAsync(long token) => _clipboard.ClearIfUnchangedAsync(token);

    /// <summary>Finder で選択状態にして表示する。</summary>
    public ShellOperationResult Reveal(string path) => RunOpen("-R", path);

    /// <summary>Finder でフォルダの中身を開く。</summary>
    public ShellOperationResult OpenFolder(string folder) => RunOpen("-a", "Finder", folder); // Foo.app などのパッケージを起動させず、フォルダとして開く

    public ShellOperationResult OpenTerminal(string folder) => RunOpen("-a", "Terminal", folder);

    public ShellOperationResult ShowProperties(string path)
        => ShellOperationResult.Failed("macOS ではプロパティ画面を利用できません。");

    private static ShellOperationResult Rename(string path, string newName)
    {
        if (!IsUsableName(newName))
        {
            return ShellOperationResult.Failed($"「{newName}」は名前として使えません。");
        }

        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is null)
        {
            return ShellOperationResult.Failed("この項目の名前は変更できません。");
        }

        var isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full))
        {
            return ShellOperationResult.Failed($"項目が見つかりません: {full}");
        }

        var target = Path.Combine(parent, newName);
        try
        {
            if (string.Equals(full, target, StringComparison.Ordinal))
            {
                return ShellOperationResult.Success([new ShellTransfer(full, target)]);
            }

            if (string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
            {
                return RenameCaseOnly(full, target, parent, newName, isDirectory);
            }

            if (File.Exists(target) || Directory.Exists(target))
            {
                return ShellOperationResult.Failed(FileNameRules.ExistsMessage(newName));
            }

            MoveItem(full, target, isDirectory);
            return ShellOperationResult.Success([new ShellTransfer(full, target)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ShellOperationResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// 大文字小文字だけの改名。大文字小文字を区別しない APFS では「変更先が既にある」と見なされて通らないことがあるため、
    /// 一時名を経由する。区別するボリュームで別の名前の項目が実在するときは断る。
    /// </summary>
    private static ShellOperationResult RenameCaseOnly(string full, string target, string parent, string newName, bool isDirectory)
    {
        var oldName = Path.GetFileName(full);
        foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
        {
            if (string.Equals(Path.GetFileName(entry), newName, StringComparison.Ordinal)
                && !string.Equals(newName, oldName, StringComparison.Ordinal))
            {
                return ShellOperationResult.Failed(FileNameRules.ExistsMessage(newName));
            }
        }

        var temporary = Path.Combine(parent, $".{Guid.NewGuid():N}.fumilume-rename");
        MoveItem(full, temporary, isDirectory);
        try
        {
            MoveItem(temporary, target, isDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MoveItem(temporary, full, isDirectory);
            return ShellOperationResult.Failed(ex.Message);
        }

        return ShellOperationResult.Success([new ShellTransfer(full, target)]);
    }

    private static void MoveItem(string source, string target, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target, overwrite: false);
        }
    }

    private static bool IsUsableName(string name)
        => name.Length > 0 && name is not ("." or "..") && !name.Any(c => c is '/' or ':' || c < ' ');

    private static ShellOperationResult MoveToTrash(IReadOnlyList<string> paths)
    {
        var completed = new List<ShellTransfer>();
        string? firstError = null;
        var failedCount = 0;
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            var error = MacObjCInterop.TryMoveToTrash(full);
            if (error is null)
            {
                completed.Add(new ShellTransfer(full, string.Empty));
                continue;
            }

            failedCount++;
            firstError ??= $"「{Path.GetFileName(full)}」: {error}";
        }

        return failedCount == 0
            ? ShellOperationResult.Success(completed)
            : ShellOperationResult.Failed($"{failedCount} 件をごみ箱へ移せませんでした。{firstError}", completed);
    }

    private static ShellOperationResult CreateFile(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            using (new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            return ShellOperationResult.Success([new ShellTransfer(full, full)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ShellOperationResult.Failed(
                File.Exists(full) || Directory.Exists(full) ? FileNameRules.ExistsMessage(Path.GetFileName(full)) : ex.Message);
        }
    }

    private static ShellOperationResult CreateFolder(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full))
        {
            return ShellOperationResult.Failed(FileNameRules.ExistsMessage(Path.GetFileName(full)));
        }

        try
        {
            Directory.CreateDirectory(full);
            return ShellOperationResult.Success([new ShellTransfer(full, full)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ShellOperationResult.Failed(ex.Message);
        }
    }

    private static ShellOperationResult RunOpen(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(OpenCommand) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return ShellOperationResult.Failed("open を起動できませんでした。");
            }

            if (process.WaitForExit(OpenTimeout) && process.ExitCode != 0)
            {
                return ShellOperationResult.Failed($"open が失敗しました（終了コード {process.ExitCode}）。");
            }

            return ShellOperationResult.Success();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ShellOperationResult.Failed(ex.Message);
        }
    }
}
