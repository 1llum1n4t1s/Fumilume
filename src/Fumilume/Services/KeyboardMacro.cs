using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fumilume.Services;

/// <summary>マクロ 1 手の種類。</summary>
public enum MacroStepKind
{
    /// <summary><see cref="EditorCommandCatalog"/> のコマンドを 1 つ実行する。</summary>
    Command,

    /// <summary>文字を差し込む（選択があれば置き換える）。</summary>
    InsertText,

    /// <summary>カーソルを動かす。</summary>
    MoveCaret,

    /// <summary>カーソルの手前を 1 つ消す（Backspace）。</summary>
    DeleteBack,

    /// <summary>カーソルの後ろを 1 つ消す（Delete）。</summary>
    DeleteForward,

    /// <summary>記録時の検索条件で次の一致へ飛ぶ。</summary>
    FindNext,
}

/// <summary>カーソルの動かし方。キーの向きと単位をそのまま持つ。</summary>
public enum MacroMotion
{
    CharacterLeft,
    CharacterRight,
    WordLeft,
    WordRight,
    LineUp,
    LineDown,
    LineStart,
    LineEnd,
    DocumentStart,
    DocumentEnd,
}

/// <summary>
/// マクロ 1 手。
///
/// 種類ごとに使う項目が違うが、JSON のソース生成へ載せるため 1 つの型にまとめている
/// （多態にすると <c>PublishAot</c> でシリアライザが組み立てられない）。
/// </summary>
public sealed class MacroStep
{
    public MacroStepKind Kind { get; set; }

    /// <summary><see cref="MacroStepKind.Command"/> のときの機能番号。</summary>
    public EditorCommandId Command { get; set; }

    /// <summary><see cref="MacroStepKind.InsertText"/> の文字、または検索の文字列。</summary>
    public string Text { get; set; } = string.Empty;

    public MacroMotion Motion { get; set; }

    /// <summary>移動しながら選択を伸ばす（Shift を押しながらの移動）。</summary>
    public bool ExtendSelection { get; set; }

    public bool MatchCase { get; set; }

    public bool UseRegex { get; set; }

    /// <summary>一覧やツールチップに出す説明。</summary>
    public string Describe() => Kind switch
    {
        MacroStepKind.Command => EditorCommandCatalog.All
            .FirstOrDefault(command => command.Id == Command)?.Title ?? Command.ToString(),
        MacroStepKind.InsertText => $"入力 {DescribeText()}",
        MacroStepKind.MoveCaret => ExtendSelection ? $"{DescribeMotion()}へ選択" : $"{DescribeMotion()}へ移動",
        MacroStepKind.DeleteBack => "前を削除",
        MacroStepKind.DeleteForward => "後ろを削除",
        MacroStepKind.FindNext => $"次を検索 {DescribeText()}",
        _ => Kind.ToString(),
    };

    private string DescribeText()
    {
        var shown = Text.Replace("\n", "⏎", StringComparison.Ordinal)
            .Replace("\t", "⇥", StringComparison.Ordinal);
        return shown.Length > 20 ? $"「{shown[..20]}…」" : $"「{shown}」";
    }

    private string DescribeMotion() => Motion switch
    {
        MacroMotion.CharacterLeft => "左",
        MacroMotion.CharacterRight => "右",
        MacroMotion.WordLeft => "前の単語",
        MacroMotion.WordRight => "次の単語",
        MacroMotion.LineUp => "上の行",
        MacroMotion.LineDown => "下の行",
        MacroMotion.LineStart => "行頭",
        MacroMotion.LineEnd => "行末",
        MacroMotion.DocumentStart => "文書の先頭",
        MacroMotion.DocumentEnd => "文書の末尾",
        _ => Motion.ToString(),
    };
}

/// <summary>名前を付けて保存したマクロ 1 本。</summary>
public sealed class KeyboardMacro
{
    public string Name { get; set; } = string.Empty;

    public List<MacroStep> Steps { get; set; } = [];

    [JsonIgnore]
    public string Summary => $"{Steps.Count:N0} 手";
}

/// <summary>保存したマクロの一覧。</summary>
public sealed class MacroLibrary
{
    public List<KeyboardMacro> Macros { get; set; } = [];
}

// PublishAot=true のためリフレクションベースのシリアライザは使えない。ソース生成を通す。
// 列挙体は名前で書く。番号で書くと、あとから値を並べ替えたときに保存済みマクロの意味が変わる。
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MacroLibrary))]
internal sealed partial class MacroJsonContext : JsonSerializerContext;

/// <summary>
/// 保存したマクロの読み書き（<c>%LocalAppData%\Fumilume\macros.json</c>）。
///
/// 設定と分けているのは、設定が「小さくて頻繁に読み書きする正本」だから。マクロは
/// 手数によっては長くなるうえ、書けなくてもアプリの動作には影響しない。
/// 読み込みは常に成功し、壊れていれば「マクロ無し」として扱う（起動できないほうが困る）。
/// </summary>
public static class MacroStore
{
    /// <summary>保存できる本数。秀丸のキーボードマクロ登録枠（10 本）より少し広く取る。</summary>
    public const int MaximumMacros = 20;

    /// <summary>1 本に積める手数。記録が暴走したときの歯止め。</summary>
    public const int MaximumSteps = 5000;

    private static string LibraryPath => Path.Combine(AppStoragePaths.Directory, "macros.json");

    public static MacroLibrary Load()
    {
        try
        {
            if (!File.Exists(LibraryPath))
            {
                return new MacroLibrary();
            }

            var parsed = JsonSerializer.Deserialize(
                File.ReadAllText(LibraryPath),
                MacroJsonContext.Default.MacroLibrary);
            if (parsed is null)
            {
                return new MacroLibrary();
            }

            // 名前が空のものと上限超過は、読んだ時点で落としておく（保存し直しで正される）。
            parsed.Macros = [.. parsed.Macros
                .Where(macro => !string.IsNullOrWhiteSpace(macro.Name))
                .Take(MaximumMacros)];
            return parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.For("Fumilume.MacroStore").Warn("マクロを読み込めませんでした。", ex);
            return new MacroLibrary();
        }
    }

    /// <returns>書けたとき <see langword="true"/>。</returns>
    public static bool Save(MacroLibrary library)
    {
        try
        {
            var json = JsonSerializer.Serialize(library, MacroJsonContext.Default.MacroLibrary);
            AtomicFile.WriteAllText(LibraryPath, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppLogger.For("Fumilume.MacroStore").Warn("マクロを保存できませんでした。", ex);
            return false;
        }
    }
}
