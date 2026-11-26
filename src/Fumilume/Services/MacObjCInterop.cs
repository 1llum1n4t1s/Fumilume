using System.Runtime.InteropServices;

namespace Fumilume.Services;

/// <summary>Objective-C ランタイムの最小限の呼び出し。ごみ箱へ移す <c>NSFileManager trashItemAtURL:resultingItemURL:error:</c> だけに使う。</summary>
internal static partial class MacObjCInterop
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const int RtldLazy = 1;

    /// <summary>
    /// <paramref name="path"/> をごみ箱へ移す。成功なら null、失敗なら理由を返す（完全削除へは切り替えない）。
    /// 呼び出しスレッドに autorelease pool が無くても漏らさないよう、ここで pool を作る。
    /// </summary>
    public static string? TryMoveToTrash(string path)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            if (dlopen(Foundation, RtldLazy) == 0)
            {
                return "Foundation を読み込めませんでした。";
            }

            var fileManager = MsgSend(GetClass("NSFileManager"), Sel("defaultManager"));
            var url = MakeFileUrl(path);
            if (fileManager == 0 || url == 0)
            {
                return "ごみ箱への移動を準備できませんでした。";
            }

            var ok = MsgSendTrash(fileManager, Sel("trashItemAtURL:resultingItemURL:error:"), url, 0, out var error);
            return ok != 0 ? null : DescribeError(error);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static nint MakeFileUrl(string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(path + '\0');
        nint text;
        unsafe
        {
            fixed (byte* p = bytes)
            {
                text = MsgSend(GetClass("NSString"), Sel("stringWithUTF8String:"), (nint)p);
            }
        }

        return text == 0 ? 0 : MsgSend(GetClass("NSURL"), Sel("fileURLWithPath:"), text);
    }

    private static string DescribeError(nint error)
    {
        if (error != 0)
        {
            var description = MsgSend(error, Sel("localizedDescription"));
            if (description != 0)
            {
                var utf8 = MsgSend(description, Sel("UTF8String"));
                if (utf8 != 0 && Marshal.PtrToStringUTF8(utf8) is { Length: > 0 } text)
                {
                    return text;
                }
            }
        }

        return "ごみ箱へ移せませんでした。";
    }

    private static nint Sel(string name) => sel_registerName(name);

    private static nint GetClass(string name) => objc_getClass(name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint MsgSend(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint MsgSend(nint receiver, nint selector, nint arg);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte MsgSendTrash(nint receiver, nint selector, nint url, nint resultingUrl, out nint error);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dlopen(string path, int mode);
}
