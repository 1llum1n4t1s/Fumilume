using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Services;

namespace Fumilume.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDocumentFileService _files;
    private readonly IEditorDialogService _dialogs;
    private int _untitledSequence;

    public MainWindowViewModel(IDocumentFileService files, IEditorDialogService dialogs)
    {
        _files = files;
        _dialogs = dialogs;
        NewDocument();
    }

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(CurrentPath))]
    private DocumentViewModel? _selectedDocument;

    [ObservableProperty]
    private string _statusMessage = "準備完了";

    [ObservableProperty]
    private bool _wordWrap;

    [ObservableProperty]
    private bool _showLineNumbers = true;

    [ObservableProperty]
    private bool _showWhitespace;

    public bool CanUndo => SelectedDocument?.CanUndo == true;

    public bool CanRedo => SelectedDocument?.CanRedo == true;

    public string WindowTitle => $"{SelectedDocument?.DisplayTitle ?? "Fumilume"} - Fumilume";

    public string CurrentPath => SelectedDocument?.PathDisplay ?? "新しいテキスト文書";

    public async Task InitializeAsync(IEnumerable<string> startupArgs)
    {
        var paths = startupArgs.Where(File.Exists).ToArray();
        if (paths.Length > 0)
        {
            await OpenPathsAsync(paths);
        }
    }

    public async Task<bool> CanCloseAsync()
    {
        foreach (var document in Documents.Where(item => item.IsModified).ToArray())
        {
            if (!await ResolveUnsavedAsync(document))
            {
                return false;
            }
        }

        return true;
    }

    [RelayCommand]
    private void NewDocument()
    {
        _untitledSequence++;
        var name = _untitledSequence == 1 ? "無題" : $"無題 {_untitledSequence}";
        var document = new DocumentViewModel(name, CloseDocumentCoreAsync);
        document.PropertyChanged += OnDocumentPropertyChanged;
        Documents.Add(document);
        SelectedDocument = document;
        StatusMessage = "新しい文書を作成しました";
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var paths = await _dialogs.PickOpenPathsAsync();
        await OpenPathsAsync(paths);
    }

    public async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(path);
            var existing = Documents.FirstOrDefault(document =>
                string.Equals(document.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                SelectedDocument = existing;
                continue;
            }

            try
            {
                var content = await _files.ReadAsync(fullPath);
                var document = new DocumentViewModel(Path.GetFileName(fullPath), CloseDocumentCoreAsync);
                document.Load(fullPath, content);
                document.PropertyChanged += OnDocumentPropertyChanged;
                Documents.Add(document);
                SelectedDocument = document;
                StatusMessage = $"{Path.GetFileName(fullPath)} を開きました";
            }
            catch (Exception ex)
            {
                await _dialogs.ShowErrorAsync(
                    "ファイルを開けません",
                    $"{fullPath}\n\n{ex.Message}");
            }
        }

        RemovePristineInitialDocument();
    }

    [RelayCommand]
    private Task SaveAsync()
        => SelectedDocument is null ? Task.CompletedTask : SaveDocumentAsync(SelectedDocument, saveAs: false);

    [RelayCommand]
    private Task SaveAsAsync()
        => SelectedDocument is null ? Task.CompletedTask : SaveDocumentAsync(SelectedDocument, saveAs: true);

    [RelayCommand]
    private Task CloseDocumentAsync(DocumentViewModel? document)
        => document is null ? Task.CompletedTask : CloseDocumentCoreAsync(document);

    [RelayCommand]
    private Task CheckForUpdatesAsync()
        => _dialogs.CheckForUpdatesAsync(manually: true);

    [RelayCommand]
    private Task ConfigureFileAssociationsAsync()
        => _dialogs.ConfigureFileAssociationsAsync();

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
        => SelectedDocument?.EditorDocument.UndoStack.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
        => SelectedDocument?.EditorDocument.UndoStack.Redo();

    [RelayCommand]
    private async Task GoToLineAsync()
    {
        var document = SelectedDocument;
        if (document is null)
        {
            return;
        }

        var line = await _dialogs.PickLineNumberAsync(document.CurrentLine, document.LineCount);
        if (line is null)
        {
            return;
        }

        document.CaretIndex = document.GetLineStartOffset(line.Value);
        StatusMessage = $"行 {line.Value:N0} へ移動しました";
    }

    private async Task CloseDocumentCoreAsync(DocumentViewModel document)
    {
        if (document.IsModified && !await ResolveUnsavedAsync(document))
        {
            return;
        }

        document.PropertyChanged -= OnDocumentPropertyChanged;
        Documents.Remove(document);
        if (Documents.Count == 0)
        {
            NewDocument();
        }
        else if (ReferenceEquals(SelectedDocument, document))
        {
            SelectedDocument = Documents[^1];
        }
    }

    private async Task<bool> ResolveUnsavedAsync(DocumentViewModel document)
    {
        var decision = await _dialogs.ConfirmUnsavedAsync(document.DisplayName);
        return decision switch
        {
            UnsavedDocumentDecision.Save => await SaveDocumentAsync(document, saveAs: false),
            UnsavedDocumentDecision.Discard => true,
            _ => false,
        };
    }

    private async Task<bool> SaveDocumentAsync(DocumentViewModel document, bool saveAs)
    {
        var path = saveAs ? null : document.FilePath;
        if (path is null)
        {
            path = await _dialogs.PickSavePathAsync(document.DisplayName);
            if (path is null)
            {
                StatusMessage = "保存をキャンセルしました";
                return false;
            }
        }

        try
        {
            await _files.WriteAsync(path, document.CreateSaveContent());
            document.MarkSaved(path);
            SelectedDocument = document;
            StatusMessage = $"{document.DisplayName} を保存しました";
            return true;
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync(
                "ファイルを保存できません",
                $"{path}\n\n{ex.Message}");
            return false;
        }
    }

    partial void OnSelectedDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(CurrentPath));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, SelectedDocument))
        {
            return;
        }

        if (args.PropertyName is nameof(DocumentViewModel.DisplayTitle) or nameof(DocumentViewModel.FilePath))
        {
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(CurrentPath));
        }

        if (args.PropertyName == nameof(DocumentViewModel.CanUndo))
        {
            OnPropertyChanged(nameof(CanUndo));
            UndoCommand.NotifyCanExecuteChanged();
        }

        if (args.PropertyName == nameof(DocumentViewModel.CanRedo))
        {
            OnPropertyChanged(nameof(CanRedo));
            RedoCommand.NotifyCanExecuteChanged();
        }
    }

    private void RemovePristineInitialDocument()
    {
        if (Documents.Count <= 1)
        {
            return;
        }

        var pristine = Documents.FirstOrDefault(document =>
            document.FilePath is null && !document.IsModified && document.Text.Length == 0);
        if (pristine is null)
        {
            return;
        }

        pristine.PropertyChanged -= OnDocumentPropertyChanged;
        Documents.Remove(pristine);
    }
}
