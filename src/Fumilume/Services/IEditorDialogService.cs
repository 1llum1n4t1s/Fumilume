namespace Fumilume.Services;

public enum UnsavedDocumentDecision
{
    Save,
    Discard,
    Cancel,
}

public interface IEditorDialogService
{
    Task<IReadOnlyList<string>> PickOpenPathsAsync();

    Task<string?> PickSavePathAsync(string suggestedFileName);

    Task<UnsavedDocumentDecision> ConfirmUnsavedAsync(string documentName);

    Task ShowErrorAsync(string title, string message);

    Task<int?> PickLineNumberAsync(int currentLine, int maximumLine);

    /// <summary>1 行ぶんの文字列を尋ねる。取り消したときは <see langword="null"/>。</summary>
    Task<string?> PromptTextAsync(string title, string message, string initialText);

    /// <summary>はい / いいえを尋ねる。</summary>
    Task<bool> ConfirmAsync(string title, string message);

    Task CheckForUpdatesAsync(bool manually);
}
