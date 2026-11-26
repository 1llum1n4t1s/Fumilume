using System.Text;

namespace Fumilume.Services;

/// <summary>
/// ファイル名・パスに関する純粋関数（検証、比較、URI、引用、コピー名）。
///
/// 名前の検証方針: Windows では Windows の規則（禁止文字・予約名・末尾の点と空白）を適用する。
/// macOS では Windows の規則までは課さず、OS が使えない文字（<c>/</c>、<c>:</c>、NUL）と空・<c>.</c>・<c>..</c> だけを断る。
/// Mac 上で作ったファイルを Windows へ持ち出せなくなる懸念はあるが、既存の（Windows 規則に反する）名前の改名を
/// Mac で妨げないことを優先する。<c>windowsRules</c> を明示する経路はテストと他 OS の判定に使う。
/// </summary>
public static class FileNameRules
{
    private const string WindowsInvalidChars = "\\/:*?\"<>|";

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>入力の前後の空白を除いた、実際に使う名前。</summary>
    public static string Normalize(string input) => input.Trim();

    /// <summary>現在の OS の規則で名前（区切りを含まない 1 要素）を検証する。問題なければ null、あればエラー文言。</summary>
    public static string? Validate(string input) => Validate(input, OperatingSystem.IsWindows());

    /// <summary>
    /// 名前を検証する。問題なければ null、あればそのまま画面へ出せる日本語のエラー文言。
    /// 空、<c>.</c>・<c>..</c>、区切りや禁止文字、Windows 規則では予約名（拡張子付きを含む）と末尾の点・空白を断る。
    /// </summary>
    public static string? Validate(string input, bool windowsRules)
    {
        var name = Normalize(input);
        if (name.Length == 0)
        {
            return "ファイル名またはフォルダ名を入力してください。";
        }

        if (name is "." or ".." || HasInvalidChar(name, windowsRules))
        {
            return InvalidNameMessage(name);
        }

        if (windowsRules && (name.EndsWith('.') || name.EndsWith(' ') || IsReservedDeviceName(name)))
        {
            return InvalidNameMessage(name);
        }

        return null;
    }

    /// <summary>同名のファイル・フォルダが既にあるときのエラー文言。</summary>
    public static string ExistsMessage(string name)
        => $"「{name}」というファイルまたはフォルダは、この場所に既にあります。別の名前を指定してください。";

    private static string InvalidNameMessage(string name)
        => $"「{name}」はファイル名またはフォルダ名として使えません。別の名前を指定してください。";

    private static bool HasInvalidChar(string name, bool windowsRules)
    {
        foreach (var c in name)
        {
            if (c < ' ' || (windowsRules ? WindowsInvalidChars.Contains(c) : c is '/' or ':'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Windows の予約デバイス名か（<c>CON</c>・<c>con.txt</c>・<c>NUL .log</c> など拡張子付きも該当）。</summary>
    public static bool IsReservedDeviceName(string name)
        => ReservedDeviceNames.Contains(name.Split('.')[0].TrimEnd());

    /// <summary>
    /// <paramref name="path"/> が <paramref name="ancestor"/> 自身かその配下か。大文字小文字を区別せず、
    /// 区切り文字を正規化し、末尾の区切りを無視する（<c>C:\a</c> と <c>C:\ab</c> は別）。
    /// </summary>
    public static bool IsSameOrDescendant(string path, string ancestor)
    {
        var p = NormalizePath(path);
        var a = NormalizePath(ancestor);
        if (string.Equals(p, a, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = a.EndsWith(Path.DirectorySeparatorChar) ? a : a + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>2 つのパスが同じ場所か（大文字小文字・区切り・末尾の区切りを無視）。</summary>
    public static bool ArePathsEqual(string left, string right)
        => string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>ドライブ直下（<c>C:\</c>）やファイルシステムの根（<c>/</c>、UNC の共有ルート）そのものか。</summary>
    public static bool IsFileSystemRoot(string path)
    {
        var normalized = NormalizePath(path);
        var root = Path.GetPathRoot(normalized);
        return !string.IsNullOrEmpty(root)
            && string.Equals(normalized.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var full = path;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 解決できないパスは入力のまま比較する。
        }

        // Path.GetFullPath は Windows で '/' を '\' にそろえる。Mac では '\' は名前の一部なので触らない。
        if (full.Length <= 1)
        {
            return full;
        }

        var root = Path.GetPathRoot(full) ?? string.Empty;
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }

    /// <summary>
    /// ローカルパスを file URI にする。空白・非 ASCII・<c>#</c>・<c>%</c> はパーセントエンコードし、
    /// UNC は <c>file://server/share/...</c> にする。作れないときは null。
    /// </summary>
    public static string? ToFileUri(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            var absolute = new Uri(path, UriKind.Absolute).AbsoluteUri;
            return EscapeNonAscii(absolute);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string EscapeNonAscii(string uri)
    {
        if (uri.All(c => c < 0x80))
        {
            return uri;
        }

        var builder = new StringBuilder(uri.Length * 2);
        var buffer = new byte[4];
        foreach (var rune in uri.EnumerateRunes())
        {
            if (rune.Value < 0x80)
            {
                builder.Append((char)rune.Value);
                continue;
            }

            var length = rune.EncodeToUtf8(buffer);
            for (var i = 0; i < length; i++)
            {
                builder.Append('%').Append(buffer[i].ToString("X2"));
            }
        }

        return builder.ToString();
    }

    /// <summary>パスを <c>"パス"</c> の形（二重引用符で囲む）にする。「パスのコピー」の形式。</summary>
    public static string ToQuotedPath(string path) => $"\"{path}\"";

    /// <summary>Markdown のリンク <c>[名前](file:///...)</c> にする。URI にできないときは null。</summary>
    public static string? ToMarkdownLink(string title, string path)
    {
        var uri = ToFileUri(path);
        if (uri is null)
        {
            return null;
        }

        var escapedTitle = title.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");
        var escapedUri = uri.Replace("(", "%28").Replace(")", "%29");
        return $"[{escapedTitle}]({escapedUri})";
    }

    /// <summary>
    /// 同じフォルダへコピーするときの名前。<paramref name="number"/> が 1 以下は「名前 のコピー.拡張子」、
    /// 2 以上は「名前 のコピー 2.拡張子」。フォルダと、拡張子の無い名前・先頭が点だけの名前（<c>.gitignore</c>）は末尾に付ける。
    /// </summary>
    public static string GetCopyName(string leafName, int number, bool isDirectory)
    {
        var extension = isDirectory ? string.Empty : Path.GetExtension(leafName);
        if (extension.Length == leafName.Length)
        {
            extension = string.Empty;
        }

        var stem = leafName[..^extension.Length];
        var suffix = number <= 1 ? " のコピー" : $" のコピー {number}";
        return stem + suffix + extension;
    }
}
