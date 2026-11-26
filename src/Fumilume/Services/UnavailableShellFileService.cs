namespace Fumilume.Services;

/// <summary>シェル操作を提供しない既定の実装。何もせず、すべて Failed を返す。</summary>
public sealed class UnavailableShellFileService : IShellFileService
{
    private const string Message = "この環境ではファイル操作を利用できません。";

    public bool CanShowProperties => false;

    public Task<ShellOperationResult> RenameAsync(string path, string newName)
        => Task.FromResult(ShellOperationResult.Failed(Message));

    public Task<ShellOperationResult> MoveToTrashAsync(IReadOnlyList<string> paths)
        => Task.FromResult(ShellOperationResult.Failed(Message));

    public Task<ShellOperationResult> TransferAsync(
        IReadOnlyList<string> sources, string destinationFolder, bool move, bool renameOnCollision)
        => Task.FromResult(ShellOperationResult.Failed(Message));

    public Task<ShellOperationResult> CreateFileAsync(string path)
        => Task.FromResult(ShellOperationResult.Failed(Message));

    public Task<ShellOperationResult> CreateFolderAsync(string path)
        => Task.FromResult(ShellOperationResult.Failed(Message));

    public Task<bool> SetFileClipboardAsync(IReadOnlyList<string> paths, bool isCut) => Task.FromResult(false);

    public Task<FileClipboardContent?> GetFileClipboardAsync() => Task.FromResult<FileClipboardContent?>(null);

    public Task<bool> HasFileClipboardAsync() => Task.FromResult(false);

    public Task<bool> ClearFileClipboardIfUnchangedAsync(long token) => Task.FromResult(false);

    public ShellOperationResult Reveal(string path) => ShellOperationResult.Failed(Message);

    public ShellOperationResult OpenFolder(string folder) => ShellOperationResult.Failed(Message);

    public ShellOperationResult OpenTerminal(string folder) => ShellOperationResult.Failed(Message);

    public ShellOperationResult ShowProperties(string path) => ShellOperationResult.Failed(Message);
}
