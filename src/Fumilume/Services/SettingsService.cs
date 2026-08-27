using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fumilume.Services;

/// <summary>アプリ設定（%LocalAppData%\Fumilume\settings.json に永続化）。</summary>
public sealed class AppSettings
{
    // ===== 表示 =====
    public bool ShowLineNumbers { get; set; } = true;

    public bool WordWrap { get; set; }

    /// <summary>折り返した続きの行を、元の行の字下げに合わせるか（sakura の「折り返し桁の字下げ」相当）。</summary>
    public bool InheritWordWrapIndentation { get; set; } = true;

    public bool HighlightCurrentLine { get; set; } = true;

    /// <summary>指定桁に縦線を引く（sakura のタイプ別設定『スクリーン』の指定桁縦線）。</summary>
    public bool ShowColumnRuler { get; set; }

    public int ColumnRulerPosition { get; set; } = AppSettingsDefaults.ColumnRulerPosition;

    /// <summary>行の高さの倍率。1.0 が既定。</summary>
    public double LineHeightFactor { get; set; } = AppSettingsDefaults.LineHeightFactor;

    // ===== 記号の表示（sakura のタイプ別設定『スクリーン』相当。sakura と同じく種類ごとに切る） =====
    public bool ShowSpaces { get; set; }

    public bool ShowTabs { get; set; }

    public bool ShowEndOfLine { get; set; }

    /// <summary>制御文字を枠付きで見せる。</summary>
    public bool ShowControlCharacters { get; set; } = true;

    // ===== フォント =====
    public string FontFamily { get; set; } = AppSettingsDefaults.FontFamily;

    public double FontSize { get; set; } = AppSettingsDefaults.FontSize;

    // ===== 編集 =====
    public int IndentationSize { get; set; } = AppSettingsDefaults.IndentationSize;

    public bool ConvertTabsToSpaces { get; set; }

    /// <summary>Tab キーを編集領域が受け取る（OFF だと次のコントロールへフォーカスが移る）。</summary>
    public bool AcceptsTab { get; set; } = true;

    /// <summary>Alt+ドラッグの矩形選択（sakura の矩形選択）。</summary>
    public bool EnableRectangularSelection { get; set; } = true;

    /// <summary>行末より右へもカーソルを置ける（sakura の「フリーカーソルモード」）。</summary>
    public bool EnableVirtualSpace { get; set; }

    /// <summary>選択したテキストをドラッグして動かす（sakura の「OLE によるドラッグ＆ドロップ」）。</summary>
    public bool EnableTextDragDrop { get; set; } = true;

    /// <summary>選択が無いときのコピー・切り取りを行全体に効かせる（sakura の「選択なしでコピー」）。</summary>
    public bool CutCopyWholeLine { get; set; } = true;

    /// <summary>最終行より下へスクロールできるか。</summary>
    public bool AllowScrollBelowDocument { get; set; } = true;

    /// <summary>Insert キーで上書きモードへ切り替えられるか（sakura の「挿入／上書きモード切り替え」）。</summary>
    public bool AllowToggleOverstrikeMode { get; set; } = true;

    /// <summary>文字を打っている間はマウスカーソルを隠す。</summary>
    public bool HideCursorWhileTyping { get; set; } = true;

    /// <summary>URL をクリックで開けるようにする（sakura の「クリッカブル URL」）。</summary>
    public bool EnableHyperlinks { get; set; } = true;

    // ===== 検索（sakura の共通設定『検索』相当） =====
    /// <summary>検索の既定で大文字小文字を区別する。</summary>
    public bool SearchMatchCase { get; set; }

    /// <summary>検索の既定を正規表現にする。</summary>
    public bool SearchUseRegex { get; set; }

    /// <summary>検索を開くときカーソル位置の単語を初期値にする（sakura の m_bCaretTextForSearch）。</summary>
    public bool SearchUseCaretWord { get; set; } = true;

    // ===== ファイル（sakura の共通設定『ファイル』『バックアップ』相当） =====
    /// <summary>開き直したときに前回のカーソル位置へ戻す（sakura の m_bRestoreCurPosition）。</summary>
    public bool RestoreCaretPosition { get; set; } = true;

    /// <summary>大きなファイルを開く前に確認する（sakura の m_bAlertIfLargeFile）。</summary>
    public bool WarnOnLargeFile { get; set; } = true;

    /// <summary>確認を出し始めるファイルサイズ（MB）。</summary>
    public int LargeFileThresholdMegabytes { get; set; } = AppSettingsDefaults.LargeFileThresholdMegabytes;

    /// <summary>上書き保存の前に .bak を作る（sakura の m_bBackUp）。</summary>
    public bool CreateBackupOnSave { get; set; }

    /// <summary>終了時に確認する（sakura の m_bExitConfirm）。未保存が無いときも尋ねる。</summary>
    public bool ConfirmOnExit { get; set; }

    /// <summary>前回のカーソル位置を覚えておく入れ物（パス → 文字位置）。</summary>
    public Dictionary<string, int> CaretPositions { get; set; } = [];

    // ===== 外観 =====
    /// <summary>"System" / "Light" / "Dark"。列挙体ではなく文字列なのは、未知の値が入っても
    /// 既定へ落として読み込みを続けられるようにするため。</summary>
    public string ThemeMode { get; set; } = AppSettingsDefaults.ThemeMode;

    public bool UseAcrylic { get; set; } = true;

    /// <summary>左のタブ一覧の 1 行の厚み（px）。</summary>
    public double TabHeight { get; set; } = AppSettingsDefaults.TabHeight;

    // ===== ウィンドウ =====
    public bool RememberWindowBounds { get; set; } = true;

    /// <summary>前回のウィンドウサイズ（0 以下なら既定値を使う）。</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    /// <summary>前回のウィンドウ位置（<see cref="int.MinValue"/> なら OS 既定）。</summary>
    public int WindowX { get; set; } = int.MinValue;

    public int WindowY { get; set; } = int.MinValue;

    public bool WindowMaximized { get; set; }

    // ===== 更新 =====
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>設定タブを開いた状態で終了したか。次回起動時に同じ状態へ戻す。</summary>
    public bool SettingsTabOpen { get; set; }
}

/// <summary>既定値の正本。<see cref="AppSettings"/> の初期化と <see cref="SettingsService"/> の
/// 正規化で同じ値を参照するために切り出してある。</summary>
internal static class AppSettingsDefaults
{
    public const string FontFamily = "Cascadia Mono";
    public const double FontSize = 15;
    public const int IndentationSize = 4;
    public const string ThemeMode = "System";
    public const int ColumnRulerPosition = 80;
    public const double LineHeightFactor = 1.0;
    public const int LargeFileThresholdMegabytes = 32;
    public const double TabHeight = 42;

    public const double MinimumFontSize = 8;
    public const double MaximumFontSize = 48;
    public const int MinimumIndentationSize = 1;
    public const int MaximumIndentationSize = 16;
    public const int MinimumColumnRulerPosition = 4;
    public const int MaximumColumnRulerPosition = 512;
    public const double MinimumLineHeightFactor = 0.8;
    public const double MaximumLineHeightFactor = 2.5;
    public const int MinimumLargeFileThresholdMegabytes = 1;
    public const int MaximumLargeFileThresholdMegabytes = 4096;

    /// <summary>タブの厚みの下限は、閉じるボタン（24px）とアイコンが潰れない高さで決めてある。</summary>
    public const double MinimumTabHeight = 28;
    public const double MaximumTabHeight = 72;

    /// <summary>覚えておくカーソル位置の件数。無制限だと settings.json が延々と太る。</summary>
    public const int MaximumCaretPositions = 200;

    public static readonly string[] ThemeModes = ["System", "Light", "Dark"];
}

// PublishAot=true のためリフレクションベースのシリアライザは使えない。ソース生成を通す。
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>設定の読み書き。読み込みは常に成功し、壊れていれば既定値を返す。</summary>
public static class SettingsService
{
    private static string SettingsPath => Path.Combine(AppStoragePaths.Directory, "settings.json");

    private static string BackupPath => SettingsPath + ".bak";

    /// <summary>設定を読む。ファイルが無い・壊れている場合は既定値を返す（例外は投げない）。</summary>
    public static AppSettings Load()
    {
        if (TryRead(SettingsPath, out var settings))
        {
            return settings;
        }

        // 本体が壊れていたときだけ退避コピーを見る（前回の正常な内容が残っている）。
        return TryRead(BackupPath, out var backup) ? backup : new AppSettings();
    }

    /// <summary>設定を書く。書き込めない環境（読み取り専用など）でも例外を外へ出さない。</summary>
    public static void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(Normalize(settings), SettingsJsonContext.Default.AppSettings);
            AtomicFile.WriteAllText(SettingsPath, json, BackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 設定が保存できないのは機能低下であって停止理由ではない。次回は既定値で起動する。
        }
    }

    private static bool TryRead(string path, out AppSettings settings)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize(text, SettingsJsonContext.Default.AppSettings);
                if (parsed is not null)
                {
                    settings = Normalize(parsed);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 壊れた JSON は既定値で起動して上書きさせる（起動できないほうが困る）。
        }

        settings = new AppSettings();
        return false;
    }

    /// <summary>範囲外の値を安全な値へ丸める。手書き編集や旧バージョンの値がそのまま UI へ届かないようにする。</summary>
    private static AppSettings Normalize(AppSettings settings)
    {
        settings.FontSize = Math.Clamp(
            settings.FontSize,
            AppSettingsDefaults.MinimumFontSize,
            AppSettingsDefaults.MaximumFontSize);
        settings.IndentationSize = Math.Clamp(
            settings.IndentationSize,
            AppSettingsDefaults.MinimumIndentationSize,
            AppSettingsDefaults.MaximumIndentationSize);
        settings.ColumnRulerPosition = Math.Clamp(
            settings.ColumnRulerPosition,
            AppSettingsDefaults.MinimumColumnRulerPosition,
            AppSettingsDefaults.MaximumColumnRulerPosition);
        settings.LineHeightFactor = Math.Clamp(
            settings.LineHeightFactor,
            AppSettingsDefaults.MinimumLineHeightFactor,
            AppSettingsDefaults.MaximumLineHeightFactor);
        settings.LargeFileThresholdMegabytes = Math.Clamp(
            settings.LargeFileThresholdMegabytes,
            AppSettingsDefaults.MinimumLargeFileThresholdMegabytes,
            AppSettingsDefaults.MaximumLargeFileThresholdMegabytes);
        settings.TabHeight = Math.Clamp(
            settings.TabHeight,
            AppSettingsDefaults.MinimumTabHeight,
            AppSettingsDefaults.MaximumTabHeight);

        // 覚えたカーソル位置は古いものから捨てる（settings.json が青天井に太らないように）。
        if (settings.CaretPositions.Count > AppSettingsDefaults.MaximumCaretPositions)
        {
            var excess = settings.CaretPositions.Count - AppSettingsDefaults.MaximumCaretPositions;
            foreach (var key in settings.CaretPositions.Keys.Take(excess).ToList())
            {
                settings.CaretPositions.Remove(key);
            }
        }

        if (string.IsNullOrWhiteSpace(settings.FontFamily))
        {
            settings.FontFamily = AppSettingsDefaults.FontFamily;
        }

        if (!AppSettingsDefaults.ThemeModes.Contains(settings.ThemeMode, StringComparer.Ordinal))
        {
            settings.ThemeMode = AppSettingsDefaults.ThemeMode;
        }

        return settings;
    }
}
