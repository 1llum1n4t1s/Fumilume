using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Fumilume.Services;

/// <summary>1 回の IFileOperation 実行の結果。済んだ項目の実パスは呼び出し側が渡したシンクから読む。</summary>
internal readonly record struct WindowsFileOperationResult(ShellOperationOutcome Outcome, int ErrorCode)
{
    public bool IsFailed => Outcome == ShellOperationOutcome.Failed;
}

/// <summary>
/// IFileOperation（エクスプローラー本体と同じ COM API）によるファイル操作。ごみ箱への削除・進捗ダイアログ・
/// 名前競合ダイアログなど Windows 標準の UI と挙動になる。操作ごとに専用の STA スレッドで実行し、
/// 呼び出し側（UI スレッド）は待つだけで塞がらない。
/// </summary>
internal static partial class WindowsFileOperation
{
    private const uint FofRenameOnCollision = 0x0008;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmMkdir = 0x0200;
    private const uint FofNoErrorUi = 0x0400;

    /// <summary>テスト用。進捗・エラー・フォルダ作成確認のシェル UI を出さない（FOF_NOCONFIRMATION は付けない）。</summary>
    private const uint SuppressUiFlags = FofSilent | FofNoErrorUi | FofNoConfirmMkdir;

    /// <summary>削除を「ごみ箱へ」にする（FOFX_RECYCLEONDELETE）。FOF_ALLOWUNDO と併せて指定する。</summary>
    private const uint FofxRecycleOnDelete = 0x00080000;

    /// <summary>エラー時にシェルの再試行ダイアログを出さず、最初の失敗で HRESULT を返す（FOFX_EARLYFAILURE）。FOF_NOERRORUI と併用する。</summary>
    private const uint FofxEarlyFailure = 0x00100000;

    /// <summary>エラー UI を抑止していても、必要な管理者権限の確認は Windows に表示させる。</summary>
    private const uint FofxShowElevationPrompt = 0x00040000;

    private const uint CoinitApartmentThreaded = 0x2;
    private const uint ClsctxInprocServer = 0x1;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    /// <summary>進捗ダイアログでの取り消し（COPYENGINE_E_USER_CANCELLED）。</summary>
    private const int CopyEngineUserCancelled = unchecked((int)0x80270000);

    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED)。</summary>
    private const int HResultErrorCancelled = unchecked((int)0x800704C7);

    private const int CopyEngineSharingViolationSource = unchecked((int)0x80270027);
    private const int CopyEngineSharingViolationDestination = unchecked((int)0x80270028);

    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IidIFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid IidIShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>1 回の操作で作った IShellItem。操作ごとに専用スレッドが立つのでスレッド単位で持ち、
    /// スレッドが終わる前（ExecuteCore の finally）にまとめて解放する。</summary>
    [ThreadStatic]
    private static List<IShellItem>? t_shellItems;

    /// <summary>名前の変更（同じフォルダ内）。</summary>
    public static Task<WindowsFileOperationResult> RenameAsync(
        string path, string newName, WindowsFileOperationSink sink, nint owner, bool suppressUi = false)
        => RunAsync(
            FofAllowUndo | (suppressUi ? SuppressUiFlags : 0),
            "rename",
            operation => operation.RenameItem(CreateShellItem(path), newName, 0),
            sink,
            owner);

    /// <summary>ごみ箱へ削除する。<paramref name="interactive"/> が false なら最初の失敗で止める非対話フラグ、
    /// true ならシェル標準のエラー・権限確認ダイアログ付き（非対話で止まった残りの続行用）。
    /// FOF_NOCONFIRMATION は付けない（完全削除になる確認などをシェルに出させるため）。</summary>
    public static Task<WindowsFileOperationResult> DeleteAsync(
        IReadOnlyList<string> paths, bool interactive, WindowsFileOperationSink sink, nint owner)
        => RunAsync(
            GetDeleteFlags(interactive),
            "delete",
            operation =>
            {
                foreach (var path in paths)
                {
                    var hr = operation.DeleteItem(CreateShellItem(path), 0);
                    if (hr < 0)
                    {
                        return hr;
                    }
                }

                return 0;
            },
            sink,
            owner);

    /// <summary>コピーまたは移動。<paramref name="renameOnCollision"/> が true なら衝突を自動改名で置く
    /// （false の衝突はシェルの確認ダイアログ）。</summary>
    public static Task<WindowsFileOperationResult> TransferAsync(
        IReadOnlyList<string> sources,
        string destinationFolder,
        bool move,
        bool renameOnCollision,
        WindowsFileOperationSink sink,
        nint owner,
        bool suppressUi = false)
        => RunAsync(
            FofAllowUndo | (renameOnCollision ? FofRenameOnCollision : 0) | (suppressUi ? SuppressUiFlags : 0),
            move ? "move" : "copy",
            operation =>
            {
                var destination = CreateShellItem(destinationFolder);
                foreach (var source in sources)
                {
                    var item = CreateShellItem(source);
                    var hr = move
                        ? operation.MoveItem(item, destination, null, 0)
                        : operation.CopyItem(item, destination, null, 0);
                    if (hr < 0)
                    {
                        return hr;
                    }
                }

                return 0;
            },
            sink,
            owner);

    internal static uint GetDeleteFlags(bool interactive)
        => interactive
            ? FofAllowUndo | FofxRecycleOnDelete | FofxShowElevationPrompt
            : FofAllowUndo | FofxRecycleOnDelete | FofxEarlyFailure | FofNoErrorUi | FofxShowElevationPrompt;

    /// <summary>失敗コードを利用者向けの日本語にする。未知のコードは件名とコードだけ。</summary>
    internal static string DescribeError(int code)
    {
        var text = code switch
        {
            32 or 33 or CopyEngineSharingViolationSource or CopyEngineSharingViolationDestination
                => "他のプログラムが使用中のため操作できませんでした。",
            740 or unchecked((int)0x80270002) => "管理者の権限が必要なため操作できませんでした。",
            2 or 3 => "対象が見つかりませんでした。",
            5 or unchecked((int)0x80270021) or unchecked((int)0x80270022) => "アクセスが拒否されました。",
            19 => "書き込み禁止のため操作できませんでした。",
            80 or 183 => "同じ名前のファイルまたはフォルダが既にあります。",
            112 => "ディスクの空き容量が足りません。",
            123 => "使えない名前です。",
            206 => "パスが長すぎます。",
            _ => "操作に失敗しました。",
        };
        return $"{text}（コード {code}）";
    }

    /// <summary>1 回のファイル操作を専用の STA スレッドで実行する（進捗ダイアログがメッセージポンプを必要とするため）。
    /// 完了時、結果を設定する前にシンクへの書き込みは終わっている。</summary>
    private static Task<WindowsFileOperationResult> RunAsync(
        uint flags, string name, Func<IFileOperation, int> queue, WindowsFileOperationSink sink, nint owner)
    {
        var completion = new TaskCompletionSource<WindowsFileOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(ExecuteCore(flags, queue, sink, owner));
            }
            catch (Exception ex)
            {
                completion.SetResult(new WindowsFileOperationResult(
                    ShellOperationOutcome.Failed, ToErrorCode(Marshal.GetHRForException(ex))));
            }
        })
        {
            IsBackground = true,
            Name = $"Fumilume-FileOperation-{name}",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static WindowsFileOperationResult ExecuteCore(
        uint flags, Func<IFileOperation, int> queue, WindowsFileOperationSink sink, nint owner)
    {
        var initializeResult = CoInitializeEx(0, CoinitApartmentThreaded);
        var shouldUninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcEChangedMode)
        {
            return Failed(initializeResult);
        }

        IFileOperation? operation = null;
        uint? adviseCookie = null;
        try
        {
            var hr = CoCreateInstance(ClsidFileOperation, 0, ClsctxInprocServer, IidIFileOperation, out var pointer);
            if (hr < 0 || pointer == 0)
            {
                return Failed(hr < 0 ? hr : unchecked((int)0x80004005));
            }

            operation = WindowsComRelease.Wrap<IFileOperation>(pointer);

            hr = operation.SetOperationFlags(flags);
            if (hr >= 0 && owner != 0)
            {
                hr = operation.SetOwnerWindow(owner);
            }

            // シンクは操作単位で 1 回だけ登録する。登録できない操作は、個別失敗と実名が得られないので続行しない。
            if (hr >= 0)
            {
                hr = operation.Advise(sink, out var cookie);
                if (hr >= 0)
                {
                    adviseCookie = cookie;
                }
            }

            if (hr >= 0)
            {
                hr = queue(operation);
            }

            if (hr < 0)
            {
                return Failed(hr);
            }

            hr = operation.PerformOperations();
            var getAbortedResult = operation.GetAnyOperationsAborted(out var aborted);
            return ClassifyCompletion(hr, getAbortedResult, aborted, sink.FirstFailureHResult);
        }
        finally
        {
            // COM オブジェクトは、この専用 STA スレッドが終わる前に手放す。
            if (adviseCookie is { } cookie && operation is not null)
            {
                operation.Unadvise(cookie);
            }

            ReleaseShellItems();
            WindowsComRelease.Release(operation);
            if (shouldUninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private static WindowsFileOperationResult Failed(int hr)
        => new(ShellOperationOutcome.Failed, ToErrorCode(hr));

    internal static WindowsFileOperationResult ClassifyCompletion(
        int performOperationsResult, int getAnyOperationsAbortedResult, bool aborted, int? itemFailureHResult)
    {
        // 非対話フラグ下では全体 HRESULT や中断フラグだけでは項目別の失敗が消えることがあるので、項目別の結果を先に見る。
        if (itemFailureHResult is { } itemFailure)
        {
            return IsCancelled(itemFailure) ? Cancelled() : Failed(itemFailure);
        }

        if (IsCancelled(performOperationsResult))
        {
            return Cancelled();
        }

        if (performOperationsResult < 0)
        {
            return Failed(performOperationsResult);
        }

        if (getAnyOperationsAbortedResult < 0)
        {
            return Failed(getAnyOperationsAbortedResult);
        }

        return aborted ? Cancelled() : new WindowsFileOperationResult(ShellOperationOutcome.Success, 0);
    }

    private static bool IsCancelled(int hr) => hr is CopyEngineUserCancelled or HResultErrorCancelled;

    private static WindowsFileOperationResult Cancelled() => new(ShellOperationOutcome.Cancelled, 0);

    /// <summary>HRESULT_FROM_WIN32 形式（0x8007xxxx）なら Win32 エラー番号、それ以外は HRESULT のまま。</summary>
    internal static int ToErrorCode(int hr)
        => (hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? hr & 0xFFFF : hr;

    private static IShellItem CreateShellItem(string path)
    {
        var hr = SHCreateItemFromParsingName(path, 0, IidIShellItem, out var pointer);
        if (hr < 0 || pointer == 0)
        {
            if (pointer != 0)
            {
                Marshal.Release(pointer);
            }

            Marshal.ThrowExceptionForHR(hr < 0 ? hr : unchecked((int)0x80004005));
        }

        var item = WindowsComRelease.Wrap<IShellItem>(pointer);
        (t_shellItems ??= []).Add(item);
        return item;
    }

    private static void ReleaseShellItems()
    {
        if (t_shellItems is not { } items)
        {
            return;
        }

        foreach (var item in items)
        {
            WindowsComRelease.Release(item);
        }

        items.Clear();
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string path, nint bindContext, in Guid interfaceId, out nint shellItem);
}

/// <summary>source-generated COM の RCW の生成と後始末。RCW は必ず <see cref="Wrap{T}"/>（所有ポインター）か
/// <see cref="WrapBorrowed{T}"/>（借用ポインター）を通して UniqueInstance で作り、<see cref="Release"/> で参照を落とす
/// （既定の生成では参照が GC のファイナライザまで残り、STA の終了後に別スレッドで解放されてしまう）。</summary>
internal static class WindowsComRelease
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>呼び出し側が所有する COM ポインター（AddRef 済み）を RCW で包み、生ポインター側の参照を手放す。</summary>
    internal static T Wrap<T>(nint pointer) where T : class
    {
        ArgumentOutOfRangeException.ThrowIfZero(pointer);
        object rcw;
        try
        {
            rcw = Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }

        return Cast<T>(rcw);
    }

    /// <summary>借用した COM ポインター（sink の引数など）を RCW で包む。生ポインターの参照には触れない。</summary>
    internal static T WrapBorrowed<T>(nint pointer) where T : class
    {
        ArgumentOutOfRangeException.ThrowIfZero(pointer);
        return Cast<T>(Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance));
    }

    internal static void Release(object? comObject)
        => (comObject as ComObject)?.FinalRelease();

    private static T Cast<T>(object rcw) where T : class
    {
        try
        {
            return (T)rcw;
        }
        catch
        {
            Release(rcw);
            throw;
        }
    }
}

[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig]
    int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);

    [PreserveSig]
    int GetParent(out nint ppsi);

    [PreserveSig]
    int GetDisplayName(int sigdnName, out nint ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(nint psi, uint hint, out int piOrder);
}

/// <summary>Explorer 本体と同じファイル操作 COM。メソッドは vtable 順に並べる必要があるため、
/// 使わないメソッドも省略せず宣言する（使わないものは引数を nint のままにしてある）。</summary>
[GeneratedComInterface]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
internal partial interface IFileOperation
{
    [PreserveSig]
    int Advise(IFileOperationProgressSink sink, out uint cookie);

    [PreserveSig]
    int Unadvise(uint cookie);

    [PreserveSig]
    int SetOperationFlags(uint flags);

    [PreserveSig]
    int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);

    [PreserveSig]
    int SetProgressDialog(nint popupDialog);

    [PreserveSig]
    int SetProperties(nint propertiesArray);

    [PreserveSig]
    int SetOwnerWindow(nint owner);

    [PreserveSig]
    int ApplyPropertiesToItem(IShellItem item);

    [PreserveSig]
    int ApplyPropertiesToItems(nint items);

    [PreserveSig]
    int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, nint sink);

    [PreserveSig]
    int RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string newName);

    [PreserveSig]
    int MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, nint sink);

    [PreserveSig]
    int MoveItems(nint items, IShellItem destinationFolder);

    [PreserveSig]
    int CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, nint sink);

    [PreserveSig]
    int CopyItems(nint items, IShellItem destinationFolder);

    [PreserveSig]
    int DeleteItem(IShellItem item, nint sink);

    [PreserveSig]
    int DeleteItems(nint items);

    [PreserveSig]
    int NewItem(
        IShellItem destinationFolder,
        uint fileAttributes,
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string? templateName,
        nint sink);

    [PreserveSig]
    int PerformOperations();

    [PreserveSig]
    int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}
