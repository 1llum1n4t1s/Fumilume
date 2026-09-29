namespace Fumilume.Services;

public sealed record SupportedFileType(string Extension, string Description, bool IsEditable = true);

/// <summary>ファイル選択ダイアログとOS別の関連付けが共有する対応形式。</summary>
public static class SupportedFileTypes
{
    public static IReadOnlyList<SupportedFileType> All { get; } =
    [
        new(".txt", "テキスト文書 (.txt)"),
        new(".md", "Markdown文書 (.md)"),
        new(".pdf", "PDF文書 (.pdf)", IsEditable: false),
        new(".log", "ログファイル (.log)"),
        new(".csv", "CSVファイル (.csv)"),
        new(".json", "JSONファイル (.json)"),
        new(".xml", "XMLファイル (.xml)"),
        new(".yaml", "YAMLファイル (.yaml)"),
        new(".yml", "YAMLファイル (.yml)"),
        new(".ini", "設定ファイル (.ini)"),
        new(".config", "構成ファイル (.config)"),
        new(".cs", "C#ソースファイル (.cs)"),
        new(".axaml", "Avalonia XAMLファイル (.axaml)"),
        new(".js", "JavaScriptファイル (.js)"),
        new(".ts", "TypeScriptファイル (.ts)"),
        new(".html", "HTMLファイル (.html)"),
        new(".css", "CSSファイル (.css)"),
    ];
}
