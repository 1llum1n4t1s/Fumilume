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

    Task ConfigureFileAssociationsAsync();

    Task CheckForUpdatesAsync(bool manually);
}
