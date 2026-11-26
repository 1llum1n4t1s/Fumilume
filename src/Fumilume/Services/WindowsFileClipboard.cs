using System.Runtime.InteropServices;

namespace Fumilume.Services;

/// <summary>
/// Win32 クリップボードの CF_HDROP と "Preferred DropEffect" を直接読み書きし、エクスプローラーとの
/// 切り取り・コピー・貼り付けに互換を持たせる。内容の世代は <c>GetClipboardSequenceNumber</c> で表す。
/// UI スレッドから呼ぶ前提（同期 API。短時間で終わる）。
/// </summary>
internal static partial class WindowsFileClipboard
{
    private const uint CfHdrop = 15;
    private const uint GmemMoveable = 0x0002;
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const int OpenClipboardAttempts = 6;
    private const int OpenClipboardRetryDelayMs = 10;
    private const string PreferredDropEffect = "Preferred DropEffect";

    /// <summary>クリップボードにファイル（CF_HDROP）があるか。</summary>
    public static bool HasFiles() => IsClipboardFormatAvailable(CfHdrop);

    /// <summary>ファイル一覧をクリップボードへ入れる（<paramref name="cut"/> が true で切り取り）。</summary>
    public static bool SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0 || !TryOpenClipboard())
        {
            return false;
        }

        try
        {
            // 確保は EmptyClipboard より先に済ませる。先に空にすると、確保に失敗したとき利用者の元の内容まで消える。
            var hDrop = CreateHDrop(paths);
            if (hDrop == 0)
            {
                return false;
            }

            var effectFormat = RegisterClipboardFormatW(PreferredDropEffect);
            var hEffect = effectFormat == 0 ? 0 : CreateDword(cut ? DropEffectMove : DropEffectCopy);
            if (hEffect == 0 && cut)
            {
                // 効果指定が無い切り取りはコピーとして解釈される。食い違いを避けるため操作ごと失敗させる。
                _ = GlobalFree(hDrop);
                return false;
            }

            EmptyClipboard();
            if (SetClipboardData(CfHdrop, hDrop) == 0)
            {
                // 設定に失敗したときは所有権がクリップボードへ移らないので、こちらで解放する。
                _ = GlobalFree(hDrop);
                if (hEffect != 0)
                {
                    _ = GlobalFree(hEffect);
                }

                return false;
            }

            if (hEffect != 0 && SetClipboardData(effectFormat, hEffect) == 0)
            {
                _ = GlobalFree(hEffect);
                if (cut)
                {
                    EmptyClipboard();
                    return false;
                }

                // コピーは効果指定が無くても既定でコピー扱いになるので続行する。
            }

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>クリップボードのファイルを読む。ファイルが無い・読めないときは null。</summary>
    public static FileClipboardContent? TryGetFiles()
    {
        if (!IsClipboardFormatAvailable(CfHdrop) || !TryOpenClipboard())
        {
            return null;
        }

        try
        {
            var hDrop = GetClipboardData(CfHdrop);
            if (hDrop == 0)
            {
                return null;
            }

            var files = new List<string>();
            var count = DragQueryFileW(hDrop, 0xFFFFFFFF, 0, 0);
            for (uint i = 0; i < count; i++)
            {
                if (ReadPath(hDrop, i) is { Length: > 0 } path)
                {
                    files.Add(path);
                }
            }

            if (files.Count == 0)
            {
                return null;
            }

            return new FileClipboardContent(files, ReadIsCut(), GetClipboardSequenceNumber());
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>クリップボードが <paramref name="sequence"/> の世代のままなら空にする。</summary>
    public static bool ClearIfUnchanged(long sequence)
    {
        if (sequence <= 0 || !TryOpenClipboard())
        {
            return false;
        }

        try
        {
            // 確認から削除まで同じ OpenClipboard の間に行い、外部の書き換えを挟ませない。
            return GetClipboardSequenceNumber() == sequence && EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    // 項目ごとに必要な長さを問い合わせてから確保する（長いパスが黙って切り詰められないように）。
    private static unsafe string? ReadPath(nint hDrop, uint index)
    {
        var needed = DragQueryFileW(hDrop, index, 0, 0);
        if (needed == 0)
        {
            return null;
        }

        var buffer = new char[needed + 1];
        fixed (char* p = buffer)
        {
            var length = DragQueryFileW(hDrop, index, (nint)p, (uint)buffer.Length);
            return length == 0 ? null : new string(buffer, 0, (int)Math.Min(length, needed));
        }
    }

    private static bool ReadIsCut()
    {
        var effectFormat = RegisterClipboardFormatW(PreferredDropEffect);
        var hEffect = effectFormat == 0 ? 0 : GetClipboardData(effectFormat);
        if (hEffect == 0)
        {
            return false;
        }

        var pointer = GlobalLock(hEffect);
        if (pointer == 0)
        {
            return false;
        }

        try
        {
            return (Marshal.ReadInt32(pointer) & DropEffectMove) != 0;
        }
        finally
        {
            GlobalUnlock(hEffect);
        }
    }

    private static unsafe nint CreateHDrop(IReadOnlyList<string> paths)
    {
        // DROPFILES (20 bytes) + 各パス (NUL 終端) + 終端 NUL
        var joined = string.Join('\0', paths) + "\0\0";
        var bytes = 20 + joined.Length * 2;
        var handle = GlobalAlloc(GmemMoveable, (nuint)bytes);
        if (handle == 0)
        {
            return 0;
        }

        var pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            _ = GlobalFree(handle);
            return 0;
        }

        Marshal.WriteInt32(pointer, 0, 20); // pFiles
        Marshal.WriteInt32(pointer, 4, 0);  // pt.x
        Marshal.WriteInt32(pointer, 8, 0);  // pt.y
        Marshal.WriteInt32(pointer, 12, 0); // fNC
        Marshal.WriteInt32(pointer, 16, 1); // fWide
        fixed (char* source = joined)
        {
            Buffer.MemoryCopy(source, (void*)(pointer + 20), joined.Length * 2, joined.Length * 2);
        }

        GlobalUnlock(handle);
        return handle;
    }

    private static nint CreateDword(uint value)
    {
        var handle = GlobalAlloc(GmemMoveable, 4);
        if (handle == 0)
        {
            return 0;
        }

        var pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            _ = GlobalFree(handle);
            return 0;
        }

        Marshal.WriteInt32(pointer, (int)value);
        GlobalUnlock(handle);
        return handle;
    }

    /// <summary>他プロセスが一時的にクリップボードを開いている場合だけ短時間再試行する。</summary>
    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < OpenClipboardAttempts; attempt++)
        {
            if (OpenClipboard(0))
            {
                return true;
            }

            if (attempt + 1 < OpenClipboardAttempts)
            {
                Thread.Sleep(OpenClipboardRetryDelayMs);
            }
        }

        return false;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint handle);

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("shell32.dll")]
    private static partial uint DragQueryFileW(nint hDrop, uint index, nint buffer, uint cch);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    /// <summary>クリップボードへ所有権が移らなかったときだけ呼ぶ（移った後に呼ぶと二重解放になる）。</summary>
    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint handle);
}
