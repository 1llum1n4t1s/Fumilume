using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Fumilume.Services;

/// <summary>
/// IFileOperation の項目別完了通知を記録するシンク。実際に済んだ移動・コピー・改名・削除の実パス
/// （名前衝突の自動改名後の名前を含む）と、項目別の失敗 HRESULT を集める。PerformOperations の全体 HRESULT だけでは
/// 非対話フラグ下の個別失敗を成功と取り違え得るため、Post* の結果も正本にする。
/// 通知はすべて操作を実行している STA スレッド上で呼ばれるので、ロックは持たない。
/// </summary>
[GeneratedComClass]
internal sealed partial class WindowsFileOperationSink : IFileOperationProgressSink
{
    private const int SigdnDesktopAbsoluteParsing = unchecked((int)0x80028000);
    private const int SigdnFilesysPath = unchecked((int)0x80058000);

    private readonly Dictionary<string, int> _itemFailures = new(StringComparer.OrdinalIgnoreCase);
    private int? _finishFailureHResult;

    /// <summary>済んだ改名（元のパス, 新しいパス）。</summary>
    public List<ShellTransfer> Renamed { get; } = [];

    /// <summary>済んだ移動。</summary>
    public List<ShellTransfer> Moved { get; } = [];

    /// <summary>済んだコピー。</summary>
    public List<ShellTransfer> Copied { get; } = [];

    /// <summary>削除の通知が成功だった項目（Destination は空文字列）。完全に消えたかは呼び出し側が確かめる。</summary>
    public List<ShellTransfer> Deleted { get; } = [];

    /// <summary>まだ成功が確認できていない項目の失敗、または全体完了の失敗 HRESULT。</summary>
    public int? FirstFailureHResult => _itemFailures.Count > 0 ? _itemFailures.First().Value : _finishFailureHResult;

    // Post* の IShellItem 引数は呼び出し元（IFileOperation）が所有する借用ポインター。nint で受け、
    // ここで借用の RCW を作り、使い終えたら自分が AddRef した分だけを解放する。
    public int PostDeleteItem(uint flags, nint item, int deleteResult, nint newlyCreated)
    {
        WithBorrowedItems(item, newlyCreated, (shellItem, created) =>
        {
            RememberItemResult(deleteResult, shellItem, created);
            if (deleteResult >= 0 && GetName(shellItem) is { Length: > 0 } original)
            {
                Deleted.Add(new ShellTransfer(original, string.Empty));
            }
        });

        return 0;
    }

    public int PostRenameItem(uint flags, nint item, string newName, int renameResult, nint newlyCreated)
    {
        WithBorrowedItems(item, newlyCreated, (shellItem, created) => RecordTransfer(Renamed, shellItem, renameResult, created));
        return 0;
    }

    public int PostMoveItem(uint flags, nint item, nint destinationFolder, string? newName, int moveResult, nint newlyCreated)
    {
        WithBorrowedItems(item, newlyCreated, (shellItem, created) => RecordTransfer(Moved, shellItem, moveResult, created));
        return 0;
    }

    public int PostCopyItem(uint flags, nint item, nint destinationFolder, string? newName, int copyResult, nint newlyCreated)
    {
        WithBorrowedItems(item, newlyCreated, (shellItem, created) => RecordTransfer(Copied, shellItem, copyResult, created));
        return 0;
    }

    private static void WithBorrowedItems(nint item, nint newlyCreated, Action<IShellItem?, IShellItem?> body)
    {
        IShellItem? shellItem = null;
        IShellItem? created = null;
        try
        {
            shellItem = item == 0 ? null : WindowsComRelease.WrapBorrowed<IShellItem>(item);
            created = newlyCreated == 0 ? null : WindowsComRelease.WrapBorrowed<IShellItem>(newlyCreated);
            body(shellItem, created);
        }
        finally
        {
            WindowsComRelease.Release(created);
            WindowsComRelease.Release(shellItem);
        }
    }

    private void RecordTransfer(List<ShellTransfer> target, IShellItem? item, int result, IShellItem? newlyCreated)
    {
        RememberItemResult(result, item, newlyCreated);
        var source = GetName(item);
        if (result < 0
            || newlyCreated is null
            || source.Length == 0
            || GetName(newlyCreated, SigdnFilesysPath) is not { Length: > 0 } destination)
        {
            return;
        }

        target.Add(new ShellTransfer(source, destination));
    }

    private void RememberItemResult(int result, IShellItem? item, IShellItem? newlyCreated)
    {
        var path = GetName(item);
        if (result < 0)
        {
            if (path.Length == 0)
            {
                // 元項目を特定できない通知は、別項目の失敗で上書きしない。
                _itemFailures.TryAdd(path, result);
            }
            else
            {
                _itemFailures[path] = result;
            }
        }
        else if (path.Length > 0 && (result == 0 || newlyCreated is not null))
        {
            // 昇格前の REQUIRES_ELEVATION に続き、同じ項目の成功が通知される。
            _itemFailures.Remove(path);
        }
    }

    private static string GetName(IShellItem? item)
    {
        var path = GetName(item, SigdnFilesysPath);
        return path.Length > 0 ? path : GetName(item, SigdnDesktopAbsoluteParsing);
    }

    private static string GetName(IShellItem? item, int kind)
    {
        if (item is null || item.GetDisplayName(kind, out var pointer) < 0 || pointer == 0)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    public int FinishOperations(int result)
    {
        _finishFailureHResult = result < 0 ? result : null;
        return 0;
    }

    // ここから下は使わないが、vtable の並びを保つために実装は省略できない。
    public int StartOperations() => 0;

    public int PreRenameItem(uint flags, nint item, string newName) => 0;

    public int PreMoveItem(uint flags, nint item, nint destinationFolder, string? newName) => 0;

    public int PreCopyItem(uint flags, nint item, nint destinationFolder, string? newName) => 0;

    public int PreDeleteItem(uint flags, nint item) => 0;

    public int PreNewItem(uint flags, nint destinationFolder, string? newName) => 0;

    public int PostNewItem(uint flags, nint destinationFolder, string? newName, string? templateName, uint fileAttributes, int newResult, nint newItem) => 0;

    public int UpdateProgress(uint workTotal, uint workSoFar) => 0;

    public int ResetTimer() => 0;

    public int PauseTimer() => 0;

    public int ResumeTimer() => 0;
}

/// <summary>IFileOperation の進捗通知。メソッドは vtable 順に並べる必要があるため、使わないものも省略せず宣言する。
/// IShellItem 引数は呼び出し元所有の借用ポインターなので nint で受ける。</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
internal partial interface IFileOperationProgressSink
{
    [PreserveSig]
    int StartOperations();

    [PreserveSig]
    int FinishOperations(int result);

    [PreserveSig]
    int PreRenameItem(uint flags, nint item, string newName);

    [PreserveSig]
    int PostRenameItem(uint flags, nint item, string newName, int renameResult, nint newlyCreated);

    [PreserveSig]
    int PreMoveItem(uint flags, nint item, nint destinationFolder, string? newName);

    [PreserveSig]
    int PostMoveItem(uint flags, nint item, nint destinationFolder, string? newName, int moveResult, nint newlyCreated);

    [PreserveSig]
    int PreCopyItem(uint flags, nint item, nint destinationFolder, string? newName);

    [PreserveSig]
    int PostCopyItem(uint flags, nint item, nint destinationFolder, string? newName, int copyResult, nint newlyCreated);

    [PreserveSig]
    int PreDeleteItem(uint flags, nint item);

    [PreserveSig]
    int PostDeleteItem(uint flags, nint item, int deleteResult, nint newlyCreated);

    [PreserveSig]
    int PreNewItem(uint flags, nint destinationFolder, string? newName);

    [PreserveSig]
    int PostNewItem(uint flags, nint destinationFolder, string? newName, string? templateName, uint fileAttributes, int newResult, nint newItem);

    [PreserveSig]
    int UpdateProgress(uint workTotal, uint workSoFar);

    [PreserveSig]
    int ResetTimer();

    [PreserveSig]
    int PauseTimer();

    [PreserveSig]
    int ResumeTimer();
}
