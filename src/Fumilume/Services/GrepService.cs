using System.Text.RegularExpressions;

namespace Fumilume.Services;

/// <summary>フォルダを横断した検索の条件（秀丸の grep 相当）。</summary>
/// <param name="Pattern">探す文字列、または正規表現。</param>
/// <param name="Folder">検索の起点フォルダ。</param>
/// <param name="FileMask">対象にするファイル名のパターン。<c>;</c> 区切りで複数指定できる。</param>
/// <param name="IncludeSubfolders">下のフォルダも辿るか。</param>
public sealed record GrepQuery(
    string Pattern,
    string Folder,
    string FileMask,
    bool IncludeSubfolders,
    bool MatchCase,
    bool UseRegex)
{
    /// <summary>タブ名やステータスへ出す一行の説明。</summary>
    public string Describe() => $"{Pattern} — {Folder}（{FileMask}）";
}

/// <summary>見つかった 1 行。</summary>
/// <param name="Column">行頭を 1 とする一致位置。</param>
public sealed record GrepMatch(string FilePath, int LineNumber, int Column, string LineText);

/// <summary>検索の結果。途中で打ち切った場合も、そこまでの一致を返す。</summary>
public sealed record GrepResult(
    IReadOnlyList<GrepMatch> Matches,
    int SearchedFiles,
    int SkippedFiles,
    bool ReachedLimit)
{
    public static GrepResult Empty { get; } = new([], 0, 0, false);
}

public interface IGrepService
{
    Task<GrepResult> SearchAsync(GrepQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// フォルダを横断して行単位に探す。
///
/// 読み込みは <see cref="IDocumentFileService"/> を通すので、対応する文字コードの判定は
/// ファイルを開いたときと同じになる。読めないファイル（対応外の文字コード、権限、ロック）は
/// 数えたうえで飛ばす。1 件の失敗で検索全体を止めない。
/// </summary>
public sealed class GrepService(IDocumentFileService files) : IGrepService
{
    /// <summary>読み込む上限。これを超えるファイルは飛ばす（grep で開くには大きすぎる）。</summary>
    internal const long MaximumFileBytes = 16L * 1024 * 1024;

    /// <summary>集める一致の上限。これ以上は打ち切って結果を返す。</summary>
    internal const int MaximumMatches = 5000;

    /// <summary>暴走しやすい正規表現から抜けるための待ち時間。</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public async Task<GrepResult> SearchAsync(GrepQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query.Pattern) || !Directory.Exists(query.Folder))
        {
            return GrepResult.Empty;
        }

        var matcher = CreateMatcher(query);
        var matches = new List<GrepMatch>();
        var searched = 0;
        var skipped = 0;
        var reachedLimit = false;

        foreach (var path in EnumerateFiles(query))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ShouldSearch(path))
            {
                skipped++;
                continue;
            }

            string text;
            try
            {
                text = (await files.ReadAsync(path, cancellationToken)).Text;
            }
            catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or NotSupportedException)
            {
                skipped++;
                continue;
            }

            searched++;
            if (CollectMatches(path, text, matcher, matches))
            {
                reachedLimit = true;
                break;
            }
        }

        return new GrepResult(matches, searched, skipped, reachedLimit);
    }

    /// <summary>一致した位置を返す。見つからない行は <see langword="null"/>。</summary>
    private static Func<string, int?> CreateMatcher(GrepQuery query)
    {
        if (query.UseRegex)
        {
            var options = RegexOptions.None;
            if (!query.MatchCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            var regex = new Regex(query.Pattern, options, RegexTimeout);
            return line =>
            {
                try
                {
                    var match = regex.Match(line);
                    return match.Success ? match.Index + 1 : null;
                }
                catch (RegexMatchTimeoutException)
                {
                    // 1 行に時間を掛けすぎたときは、その行を諦めて次へ進む。
                    return null;
                }
            };
        }

        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return line =>
        {
            var index = line.IndexOf(query.Pattern, comparison);
            return index < 0 ? null : index + 1;
        };
    }

    /// <summary>上限に達したら <see langword="true"/>。</summary>
    private static bool CollectMatches(
        string path,
        string text,
        Func<string, int?> matcher,
        List<GrepMatch> matches)
    {
        var lineNumber = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r');
            if (matcher(line) is not { } column)
            {
                continue;
            }

            matches.Add(new GrepMatch(path, lineNumber, column, line));
            if (matches.Count >= MaximumMatches)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateFiles(GrepQuery query)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = query.IncludeSubfolders,
            IgnoreInaccessible = true,
            // 隠しフォルダ（.git など）まで開けると、探したいものが埋もれる。
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var masks = SplitMasks(query.FileMask);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mask in masks)
        {
            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateFiles(query.Folder, mask, options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (var path in found)
            {
                // マスクを複数指定すると同じファイルが複数回出ることがある。
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }

    internal static IReadOnlyList<string> SplitMasks(string fileMask)
    {
        var masks = fileMask
            .Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return masks.Length == 0 ? ["*"] : masks;
    }

    /// <summary>大きすぎるファイルと、中身が文字でないファイルを外す。</summary>
    private static bool ShouldSearch(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumFileBytes)
            {
                return false;
            }

            return info.Length == 0 || !LooksBinary(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>先頭に NUL が現れたら文字列として扱わない（画像や実行ファイルを弾く）。</summary>
    private static bool LooksBinary(string path)
    {
        Span<byte> head = stackalloc byte[512];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var content = head[..stream.Read(head)];

        // UTF-16 のテキストは NUL を含む。BOM が付いていれば中身では判断しない。
        if (content.Length >= 2 &&
            ((content[0] == 0xFF && content[1] == 0xFE) || (content[0] == 0xFE && content[1] == 0xFF)))
        {
            return false;
        }

        return content.Contains((byte)0);
    }
}
