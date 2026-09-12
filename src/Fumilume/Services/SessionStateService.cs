using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fumilume.Services;

/// <summary>前回終了時のワークスペース（タブの並び、選択、未保存の内容）。</summary>
public sealed class SessionState
{
    [JsonRequired]
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

    /// <summary>PDF のフィット方式。null は旧形式のセッション。</summary>
    public string? PdfZoomMode { get; set; }

    /// <summary>タブ一覧の先頭へ固定されていたか。旧セッションでは既定の false として扱う。</summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// 未保存の本文。JSON へは書かず、<see cref="SessionStateService"/> が
    /// <see cref="BufferFile"/> の指す別ファイルへ出し入れする。
    ///
    /// 本文を session.json へ埋め込むと、エスケープでサイズが膨らむうえに 1 文字の変更でも
    /// 全体を書き直すことになる。タブごとのファイルなら書き換えたタブだけを触れば済む。
    /// </summary>
    [JsonIgnore]
    public string? Text { get; set; }

    /// <summary>控え本文を読めなかった理由。JSONには保存せず、今回の復元判断だけに使う。</summary>
    [JsonIgnore]
    internal SessionBufferReadStatus BufferReadStatus { get; set; }
}

internal enum SessionBufferReadStatus
{
    None,
    Loaded,
    Missing,
    Unavailable,
}

internal readonly record struct SessionLoadResult(
    SessionState State,
    bool Failed,
    bool RecoveryDeferred = false);

internal readonly record struct RecoverySessionLoadResult(
    IReadOnlyList<SessionState> States,
    bool Deferred);

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
/// 公開読み込みは常に成功し、壊れていれば「セッション無し」として扱う（起動できないほうが困る）。
/// アプリの起動経路では失敗状態も受け取り、既存の一覧と控えを誤って上書きしない。
/// </summary>
public static class SessionStateService
{
    private static string SessionPath => Path.Combine(AppStoragePaths.Directory, "session.json");

    /// <summary>
    /// 主一覧を読めないあいだ、今回の編集を別系統で守る一覧。
    /// 主一覧を上書きしないため、復旧するまでは古い控えの掃除も行わない。
    /// </summary>
    private static string RecoveryDirectory => Path.Combine(AppStoragePaths.Directory, "session-recovery");

    /// <summary>未保存の本文を置くディレクトリ。</summary>
    private static string BufferDirectory => Path.Combine(AppStoragePaths.Directory, "session");

    /// <summary>前回終了時のワークスペースを読む。無い・壊れているときは空のセッションを返す。</summary>
    public static SessionState Load() => LoadWithStatus().State;

    /// <summary>一覧が無い状態と、存在する一覧を読めなかった状態を区別して読む。</summary>
    internal static SessionLoadResult LoadWithStatus()
    {
        SessionState primary;
        var primaryFailed = false;
        try
        {
            primary = ReadSession(SessionPath, BufferDirectory);
        }
        catch (FileNotFoundException)
        {
            primary = new SessionState();
        }
        catch (DirectoryNotFoundException)
        {
            primary = new SessionState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn("前回のセッションを読み込めませんでした。", ex);
            primary = new SessionState();
            primaryFailed = true;
        }

        var recovery = LoadRecoverySessions();
        if (recovery.Deferred)
        {
            return new SessionLoadResult(primary, primaryFailed, RecoveryDeferred: true);
        }

        return new SessionLoadResult(
            MergeSessions(primary, recovery.States),
            primaryFailed,
            RecoveryDeferred: false);
    }

    /// <summary>
    /// ワークスペースを書き出す。書き込めない環境でも例外を外へ出さず、成否を返す。
    ///
    /// 未保存の本文はここが唯一の退避先なので、呼び出し側は戻り値を見て
    /// 「引き継げなかったまま閉じる」ことがないようにする。
    /// </summary>
    /// <returns>本文と一覧の両方を書けたとき <see langword="true"/>。</returns>
    public static bool Save(SessionState state)
        => Save(state, clearRecovery: true);

    internal static bool Save(SessionState state, bool clearRecovery)
    {
        if (!SaveCore(state, SessionPath, BufferDirectory, "セッション"))
        {
            return false;
        }

        if (clearRecovery)
        {
            DeleteRecoveryDirectory();
        }

        // 主一覧の確定後なら、主一覧専用の置き場にある旧世代だけを安全に片付けられる。
        RemoveUnusedBuffers(state, BufferDirectory);
        return true;
    }

    /// <summary>
    /// 主一覧を読めなかった起動中の編集を別一覧へ控える。
    /// 読めない主一覧が参照している可能性のある控えは一切掃除しない。
    /// </summary>
    internal static bool SaveRecovery(SessionState state, bool replaceExisting)
    {
        var snapshotName = $"snapshot-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}";
        var pending = Path.Combine(RecoveryDirectory, $".pending-{Guid.NewGuid():N}");
        var committed = Path.Combine(RecoveryDirectory, snapshotName);
        try
        {
            Directory.CreateDirectory(pending);
            WriteBuffers(state, pending);
            var json = JsonSerializer.Serialize(state, SessionJsonContext.Default.SessionState);
            AtomicFile.WriteAllText(Path.Combine(pending, "session.json"), json);
            Directory.Move(pending, committed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDeleteDirectory(pending, "未完成の回復用セッションを片付けられませんでした。");
            AppLogger.For("Fumilume.SessionStateService").Warn("回復用セッションを保存できませんでした。", ex);
            return false;
        }

        if (replaceExisting)
        {
            DeleteRecoverySnapshotsExcept(committed);
        }

        return true;
    }

    private static bool SaveCore(SessionState state, string destination, string bufferDirectory, string target)
    {
        try
        {
            WriteBuffers(state, bufferDirectory);
            var json = JsonSerializer.Serialize(state, SessionJsonContext.Default.SessionState);
            AtomicFile.WriteAllText(destination, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn($"{target}を保存できませんでした。", ex);
            return false;
        }
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

            if (Directory.Exists(RecoveryDirectory))
            {
                Directory.Delete(RecoveryDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn("セッションを削除できませんでした。", ex);
        }
    }

    private static SessionState ReadSession(string path, string bufferDirectory)
    {
        var parsed = JsonSerializer.Deserialize(
            File.ReadAllText(path),
            SessionJsonContext.Default.SessionState);
        if (parsed?.Tabs is null)
        {
            throw new JsonException("セッションのタブ一覧がありません。");
        }

        foreach (var tab in parsed.Tabs)
        {
            if (tab is null)
            {
                throw new JsonException("セッションに不正なタブがあります。");
            }

            var buffer = ReadBuffer(bufferDirectory, tab.BufferFile);
            tab.Text = buffer.Text;
            tab.BufferReadStatus = buffer.Status;
        }

        return parsed;
    }

    private static RecoverySessionLoadResult LoadRecoverySessions()
    {
        string[] snapshots;
        try
        {
            snapshots = Directory.Exists(RecoveryDirectory)
                ? Directory.EnumerateDirectories(RecoveryDirectory, "snapshot-*")
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn("回復用セッションの一覧を読み込めませんでした。", ex);
            return new RecoverySessionLoadResult([], Deferred: true);
        }

        var states = new List<SessionState>(snapshots.Length);
        foreach (var snapshot in snapshots)
        {
            try
            {
                var state = ReadSession(Path.Combine(snapshot, "session.json"), snapshot);
                if (state.Tabs.Any(tab => tab.BufferReadStatus == SessionBufferReadStatus.Unavailable))
                {
                    return new RecoverySessionLoadResult([], Deferred: true);
                }

                states.Add(state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                AppLogger.For("Fumilume.SessionStateService").Warn(
                    $"回復用セッションを読み込めませんでした: {snapshot}",
                    ex);
                return new RecoverySessionLoadResult([], Deferred: true);
            }
        }

        return new RecoverySessionLoadResult(states, Deferred: false);
    }

    private static SessionState MergeSessions(SessionState primary, IReadOnlyList<SessionState> recoveries)
    {
        if (recoveries.Count == 0)
        {
            return primary;
        }

        var merged = new SessionState
        {
            Tabs = [.. primary.Tabs],
            SelectedTabIndex = primary.SelectedTabIndex,
            SettingsTabOpen = primary.SettingsTabOpen,
        };
        foreach (var recovery in recoveries)
        {
            var offset = merged.Tabs.Count;
            merged.Tabs.AddRange(recovery.Tabs);
            if (recovery.SelectedTabIndex >= 0 && recovery.SelectedTabIndex < recovery.Tabs.Count)
            {
                merged.SelectedTabIndex = offset + recovery.SelectedTabIndex;
            }

            merged.SettingsTabOpen |= recovery.SettingsTabOpen;
        }

        return merged;
    }

    private static void DeleteRecoveryDirectory()
    {
        TryDeleteDirectory(RecoveryDirectory, "古い回復用セッションを削除できませんでした。");
    }

    private static void DeleteRecoverySnapshotsExcept(string committed)
    {
        try
        {
            foreach (var path in Directory.EnumerateDirectories(RecoveryDirectory))
            {
                if (!string.Equals(path, committed, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectory(path, "古い回復用セッションを削除できませんでした。");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn("古い回復用セッションを列挙できませんでした。", ex);
        }
    }

    private static void TryDeleteDirectory(string path, string message)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn(message, ex);
        }
    }

    /// <summary>未保存の本文をタブごとのファイルへ書き、参照名を <see cref="SessionTabState.BufferFile"/> へ入れる。</summary>
    private static void WriteBuffers(SessionState state, string bufferDirectory)
    {
        for (var index = 0; index < state.Tabs.Count; index++)
        {
            var tab = state.Tabs[index];
            if (tab.Text is null)
            {
                // 復元中の異常で本文だけ読めなかった未保存タブは、前回の参照を維持する。
                // ここで参照を消すと、後段の掃除がまだ回収できる控えまで削除してしまう。
                if (!tab.IsModified)
                {
                    tab.BufferFile = null;
                }

                continue;
            }

            // 毎回新しい名前で書く。同じ名前を使い回すと、これから書く session.json が失敗した場合に
            // 「前回の一覧が今回の本文を指す」状態になり、前回の書きかけが別タブの内容へすり替わる。
            // 使われなくなった控えは、一覧を確定させたあとの RemoveUnusedBuffers が片付ける。
            var name = $"tab-{index}-{Guid.NewGuid():N}.txt";
            AtomicFile.WriteAllText(Path.Combine(bufferDirectory, name), tab.Text);
            tab.BufferFile = name;
        }
    }

    private static (string? Text, SessionBufferReadStatus Status) ReadBuffer(
        string bufferDirectory,
        string? bufferFile)
    {
        if (string.IsNullOrEmpty(bufferFile))
        {
            return (null, SessionBufferReadStatus.None);
        }

        // session.json が手で書き換えられていても、控えの読み込み先がディレクトリの外へ出ないようにする。
        string fileName;
        try
        {
            fileName = Path.GetFileName(bufferFile);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException("セッションの控えファイル名が不正です。", ex);
        }

        if (!string.Equals(fileName, bufferFile, StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new JsonException("セッションの控えファイル名が不正です。");
        }

        var path = Path.Combine(bufferDirectory, fileName);
        try
        {
            return (File.ReadAllText(path), SessionBufferReadStatus.Loaded);
        }
        catch (FileNotFoundException)
        {
            return (null, SessionBufferReadStatus.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return (null, SessionBufferReadStatus.Missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For("Fumilume.SessionStateService").Warn($"未保存の内容を読み込めませんでした: {path}", ex);
            return (null, SessionBufferReadStatus.Unavailable);
        }
    }

    /// <summary>
    /// 今回のセッションが参照していない控えを消す（閉じたタブと前回の版を残さない）。
    /// 後始末なので、失敗しても保存そのものは成功として扱う（次回の保存でもう一度試される）。
    /// </summary>
    private static void RemoveUnusedBuffers(SessionState state, string bufferDirectory)
    {
        if (!Directory.Exists(bufferDirectory))
        {
            return;
        }

        var used = state.Tabs
            .Select(tab => tab.BufferFile)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var path in Directory.EnumerateFiles(bufferDirectory))
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
