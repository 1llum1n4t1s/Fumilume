using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fumilume.Services;

/// <summary>前回終了時のワークスペース（タブの並び、選択、未保存の内容）。</summary>
public sealed class SessionState
{
    public List<SessionTabState> Tabs { get; set; } = [];

    /// <summary>終了時に選ばれていた <see cref="Tabs"/> の位置。復元できないタブがあってもよいように
    /// 参照ではなく添字で持ち、範囲外は復元側で捨てる。</summary>
    public int SelectedTabIndex { get; set; } = -1;

    /// <summary>設定タブを開いた状態で終了したか。</summary>
    public bool SettingsTabOpen { get; set; }
}

/// <summary><see cref="SessionTabState.Kind"/> に入る値。未知の値は文書として扱う。</summary>
public static class SessionTabKinds
{
    public const string Document = "Document";
    public const string Pdf = "Pdf";
}

/// <summary>タブ 1 枚分の復元情報。</summary>
public sealed class SessionTabState
{
    public string Kind { get; set; } = SessionTabKinds.Document;

    /// <summary>保存済みファイルのパス。未保存の新規文書では null。</summary>
    public string? FilePath { get; set; }

    /// <summary>未保存の新規文書に付いていた「無題」系の名前。</summary>
    public string? UntitledName { get; set; }

    /// <summary>終了時点でディスクの内容と違っていたか。</summary>
    public bool IsModified { get; set; }

    /// <summary>未保存の本文を控えたファイル名（<c>session</c> ディレクトリからの相対）。</summary>
    public string? BufferFile { get; set; }

    /// <summary>文字コード。列挙体ではなく文字列なのは、未知の値が入っても既定へ落として
    /// 読み込みを続けられるようにするため（<see cref="AppSettings.ThemeMode"/> と同じ方針）。</summary>
    public string Encoding { get; set; } = "Utf8";

    public string NewLine { get; set; } = "\r\n";

    public int CaretIndex { get; set; }

    public int SelectionStart { get; set; }

    public int SelectionLength { get; set; }

    public bool IsMarkdownPreview { get; set; }

    /// <summary>印の付いていた行番号。</summary>
    public List<int> Bookmarks { get; set; } = [];

    /// <summary>PDF タブで表示していたページ（1 始まり）。</summary>
    public int PdfPage { get; set; } = 1;

    /// <summary>PDF タブの拡大率。0 以下なら既定へ落とす。</summary>
    public double PdfZoom { get; set; }

    /// <summary>
    /// 未保存の本文。JSON へは書かず、<see cref="SessionStateService"/> が
    /// <see cref="BufferFile"/> の指す別ファイルへ出し入れする。
    ///
    /// 本文を session.json へ埋め込むと、エスケープでサイズが膨らむうえに 1 文字の変更でも
    /// 全体を書き直すことになる。タブごとのファイルなら書き換えたタブだけを触れば済む。
    /// </summary>
    [JsonIgnore]
    public string? Text { get; set; }
}

// PublishAot=true のためリフレクションベースのシリアライザは使えない。ソース生成を通す。
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionState))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;

/// <summary>
/// 終了時のワークスペースの読み書き。
///
/// 置き場を settings.json と分けているのは、設定が「小さくて頻繁に読み書きする正本」だから。
/// 未保存の本文（何 MB にもなりうる）を同居させると、設定の保存が本文の大きさに引きずられ、
/// 書き込みに失敗したときの被害も設定全体へ広がる。
///
/// 読み込みは常に成功し、壊れていれば「セッション無し」として扱う（起動できないほうが困る）。
/// </summary>
public static class SessionStateService
{
    private static string SessionPath => Path.Combine(AppStoragePaths.Directory, "session.json");

    /// <summary>未保存の本文を置くディレクトリ。</summary>
    private static string BufferDirectory => Path.Combine(AppStoragePaths.Directory, "session");

    /// <summary>前回終了時のワークスペースを読む。無い・壊れているときは空のセッションを返す。</summary>
    public static SessionState Load()
    {
        try
        {
            if (!File.Exists(SessionPath))
            {
                return new SessionState();
            }

            var parsed = JsonSerializer.Deserialize(
                File.ReadAllText(SessionPath),
                SessionJsonContext.Default.SessionState);
            if (parsed is null)
            {
                return new SessionState();
            }

            foreach (var tab in parsed.Tabs)
            {
                tab.Text = ReadBuffer(tab.BufferFile);
            }

            return parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn("前回のセッションを読み込めませんでした。", ex);
            return new SessionState();
        }
    }

    /// <summary>
    /// ワークスペースを書き出す。書き込めない環境でも例外を外へ出さず、成否を返す。
    ///
    /// 未保存の本文はここが唯一の退避先なので、呼び出し側は戻り値を見て
    /// 「引き継げなかったまま閉じる」ことがないようにする。
    /// </summary>
    /// <returns>本文と一覧の両方を書けたとき <see langword="true"/>。</returns>
    public static bool Save(SessionState state)
    {
        try
        {
            WriteBuffers(state);
            var json = JsonSerializer.Serialize(state, SessionJsonContext.Default.SessionState);
            AtomicFile.WriteAllText(SessionPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn("セッションを保存できませんでした。", ex);
            return false;
        }

        // 一覧が確定してからでないと古い控えを消せない（消してから確定に失敗すると前回分まで失う）。
        RemoveUnusedBuffers(state);
        return true;
    }

    /// <summary>セッションを捨てる（復元しない設定に切り替えたときの後始末）。</summary>
    public static void Clear()
    {
        try
        {
            File.Delete(SessionPath);
            if (Directory.Exists(BufferDirectory))
            {
                Directory.Delete(BufferDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn("セッションを削除できませんでした。", ex);
        }
    }

    /// <summary>未保存の本文をタブごとのファイルへ書き、参照名を <see cref="SessionTabState.BufferFile"/> へ入れる。</summary>
    private static void WriteBuffers(SessionState state)
    {
        for (var index = 0; index < state.Tabs.Count; index++)
        {
            var tab = state.Tabs[index];
            if (tab.Text is null)
            {
                tab.BufferFile = null;
                continue;
            }

            // 毎回新しい名前で書く。同じ名前を使い回すと、これから書く session.json が失敗した場合に
            // 「前回の一覧が今回の本文を指す」状態になり、前回の書きかけが別タブの内容へすり替わる。
            // 使われなくなった控えは、一覧を確定させたあとの RemoveUnusedBuffers が片付ける。
            var name = $"tab-{index}-{Guid.NewGuid():N}.txt";
            AtomicFile.WriteAllText(Path.Combine(BufferDirectory, name), tab.Text);
            tab.BufferFile = name;
        }
    }

    private static string? ReadBuffer(string? bufferFile)
    {
        if (string.IsNullOrEmpty(bufferFile))
        {
            return null;
        }

        // session.json が手で書き換えられていても、控えの読み込み先がディレクトリの外へ出ないようにする。
        var path = Path.Combine(BufferDirectory, Path.GetFileName(bufferFile));
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn($"未保存の内容を読み込めませんでした: {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// 今回のセッションが参照していない控えを消す（閉じたタブと前回の版を残さない）。
    /// 後始末なので、失敗しても保存そのものは成功として扱う（次回の保存でもう一度試される）。
    /// </summary>
    private static void RemoveUnusedBuffers(SessionState state)
    {
        if (!Directory.Exists(BufferDirectory))
        {
            return;
        }

        var used = state.Tabs
            .Select(tab => tab.BufferFile)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var path in Directory.EnumerateFiles(BufferDirectory))
            {
                if (used.Contains(Path.GetFileName(path)))
                {
                    continue;
                }

                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn("古いセッションの控えを片付けられませんでした。", ex);
        }
    }
}
