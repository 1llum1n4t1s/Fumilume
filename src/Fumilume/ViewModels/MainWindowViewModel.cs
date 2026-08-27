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

    public MainWindowViewModel(
        IDocumentFileService files,
        IEditorDialogService dialogs,
        AppSettings? settings = null)
    {
        _files = files;
        _dialogs = dialogs;
        Options = new AppOptionsViewModel(settings ?? new AppSettings());
        NewDocument();
    }

    /// <summary>タブ一覧。文書タブと設定タブが同じ並びに入る。</summary>
    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];

    /// <summary>エディタ本体と設定タブが共有するオプション。</summary>
    public AppOptionsViewModel Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(CurrentPath))]
    [NotifyPropertyChangedFor(nameof(SelectedDocument))]
    [NotifyPropertyChangedFor(nameof(IsDocumentSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(SelectedPdf))]
    [NotifyPropertyChangedFor(nameof(IsPdfSelected))]
    private WorkspaceTabViewModel? _selectedTab;

    [ObservableProperty]
    private string _statusMessage = "準備完了";

    /// <summary>開かれている設定タブ（無ければ null）。設定ビューの DataContext をここへ直接
    /// 差し込むことで、文書タブを選んでいる間に設定ビューが文書へバインドされるのを避ける。</summary>
    [ObservableProperty]
    private SettingsTabViewModel? _settingsTab;

    /// <summary>選択中のタブが文書ならそれ。設定タブのときは null。</summary>
    public DocumentViewModel? SelectedDocument => SelectedTab as DocumentViewModel;

    public bool IsDocumentSelected => SelectedTab is DocumentViewModel;

    public bool IsSettingsSelected => SelectedTab is SettingsTabViewModel;

    public PdfDocumentViewModel? SelectedPdf => SelectedTab as PdfDocumentViewModel;

    public bool IsPdfSelected => SelectedTab is PdfDocumentViewModel;

    /// <summary>開いている文書だけを取り出す（未保存確認や重複オープンの判定に使う）。</summary>
    public IEnumerable<DocumentViewModel> Documents => Tabs.OfType<DocumentViewModel>();

    public bool CanUndo => SelectedDocument?.CanUndo == true;

    public bool CanRedo => SelectedDocument?.CanRedo == true;

    public string WindowTitle => $"{SelectedTab?.TabTitle ?? "Fumilume"} - Fumilume";

    public string CurrentPath => SelectedDocument?.PathDisplay ?? SelectedPdf?.FilePath ?? "新しいテキスト文書";

    public async Task InitializeAsync(IEnumerable<string> startupArgs)
    {
        var paths = startupArgs.Where(File.Exists).ToArray();
        if (paths.Length > 0)
        {
            await OpenPathsAsync(paths);
        }

        // 前回終了時に設定タブを開いていたなら同じ状態へ戻す（選択は文書のまま）。
        if (Options.Settings.SettingsTabOpen)
        {
            EnsureSettingsTab(select: false);
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

    /// <summary>終了時に、次回復元したい状態を settings.json へ書き戻す。</summary>
    public void PersistSessionState()
    {
        Options.Settings.SettingsTabOpen = Tabs.Any(tab => tab.IsSettingsTab);
        foreach (var document in Documents)
        {
            RememberCaretPosition(document);
        }

        Options.Persist();
    }

    [RelayCommand]
    private void NewDocument()
    {
        _untitledSequence++;
        var name = _untitledSequence == 1 ? "無題" : $"無題 {_untitledSequence}";
        var document = new DocumentViewModel(name, CloseTabCoreAsync);
        document.PropertyChanged += OnDocumentPropertyChanged;
        InsertContentTab(document);
        SelectedTab = document;
        StatusMessage = "新しい文書を作成しました";
    }

    /// <summary>設定タブを開く（既に開いていればそれを選ぶ）。</summary>
    [RelayCommand]
    private void OpenSettings() => EnsureSettingsTab(select: true);

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
            var existing = Tabs.FirstOrDefault(tab => tab switch
            {
                DocumentViewModel document => string.Equals(document.FilePath, fullPath, StringComparison.OrdinalIgnoreCase),
                PdfDocumentViewModel pdf => string.Equals(pdf.FilePath, fullPath, StringComparison.OrdinalIgnoreCase),
                _ => false,
            });
            if (existing is not null)
            {
                SelectedTab = existing;
                continue;
            }

            if (!await ConfirmLargeFileAsync(fullPath))
            {
                continue;
            }

            try
            {
                if (string.Equals(Path.GetExtension(fullPath), ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var pdf = await PdfDocumentViewModel.OpenAsync(fullPath, CloseTabCoreAsync);
                    InsertContentTab(pdf);
                    SelectedTab = pdf;
                    StatusMessage = $"{Path.GetFileName(fullPath)} を開きました（{pdf.PageCount:N0} ページ）";
                    continue;
                }

                var content = await _files.ReadAsync(fullPath);
                var document = new DocumentViewModel(Path.GetFileName(fullPath), CloseTabCoreAsync);
                document.Load(fullPath, content);
                RestoreCaretPosition(document, fullPath);
                document.PropertyChanged += OnDocumentPropertyChanged;
                InsertContentTab(document);
                SelectedTab = document;
                StatusMessage = $"{Path.GetFileName(fullPath)} を開きました";
            }
            catch (Exception ex)
            {
                AppLogger.For<MainWindowViewModel>().Error($"ファイルを開けませんでした: {fullPath}", ex);
                await _dialogs.ShowErrorAsync(
                    "ファイルを開けません",
                    $"{fullPath}\n\n{ex.Message}");
            }
        }

        RemovePristineInitialDocument();
    }

    [RelayCommand(CanExecute = nameof(IsDocumentSelected))]
    private Task SaveAsync()
        => SelectedDocument is null ? Task.CompletedTask : SaveDocumentAsync(SelectedDocument, saveAs: false);

    [RelayCommand(CanExecute = nameof(IsDocumentSelected))]
    private Task SaveAsAsync()
        => SelectedDocument is null ? Task.CompletedTask : SaveDocumentAsync(SelectedDocument, saveAs: true);

    private bool CanSaveAll() => Documents.Any(document => document.IsModified);

    /// <summary>sakura の F_FILESAVEALL 相当。未保存文書の保存先選択を含めて順番に保存する。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAll))]
    private async Task SaveAllAsync()
    {
        var targets = Documents.Where(document => document.IsModified).ToArray();
        var saved = 0;
        foreach (var document in targets)
        {
            if (!await SaveDocumentAsync(document, saveAs: false))
            {
                StatusMessage = $"{saved:N0} 件を保存し、残りを中止しました";
                return;
            }

            saved++;
        }

        StatusMessage = $"{saved:N0} 件の文書を保存しました";
    }

    private bool CanReload() => SelectedDocument?.FilePath is not null;

    /// <summary>sakura の F_FILE_REOPEN 相当。現在と同じ文字コード判定でディスクから読み直す。</summary>
    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync()
    {
        if (SelectedDocument is not { FilePath: { } path } document)
        {
            return;
        }

        if (document.IsModified && !await _dialogs.ConfirmAsync(
                "ファイルを開き直す",
                $"{document.DisplayName} の未保存の変更を破棄して、ディスクから開き直しますか？"))
        {
            return;
        }

        var caret = document.CaretIndex;
        try
        {
            var content = await _files.ReadAsync(path);
            document.Load(path, content);
            document.CaretIndex = Math.Clamp(caret, 0, document.EditorDocument.TextLength);
            StatusMessage = $"{document.DisplayName} を開き直しました";
        }
        catch (Exception ex)
        {
            AppLogger.For<MainWindowViewModel>().Error($"ファイルを開き直せませんでした: {path}", ex);
            await _dialogs.ShowErrorAsync("ファイルを開き直せません", $"{path}\n\n{ex.Message}");
        }
    }

    private bool CanToggleMarkdownPreview() => SelectedDocument?.CanShowMarkdownPreview == true;

    [RelayCommand(CanExecute = nameof(CanToggleMarkdownPreview))]
    private void ToggleMarkdownPreview()
    {
        SelectedDocument?.ToggleMarkdownPreview();
        StatusMessage = SelectedDocument?.IsMarkdownPreview == true
            ? "Markdown プレビューを表示しました"
            : "Markdown の編集表示へ戻しました";
    }

    [RelayCommand]
    private Task CloseTabAsync(WorkspaceTabViewModel? tab)
        => tab is null ? Task.CompletedTask : CloseTabCoreAsync(tab);

    [RelayCommand]
    private Task CheckForUpdatesAsync()
        => _dialogs.CheckForUpdatesAsync(manually: true);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
        => SelectedDocument?.EditorDocument.UndoStack.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
        => SelectedDocument?.EditorDocument.UndoStack.Redo();

    [RelayCommand(CanExecute = nameof(IsDocumentSelected))]
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

    /// <summary>設定タブは常に一覧の末尾に置き、文書タブはその手前へ追加する。</summary>
    private void InsertContentTab(WorkspaceTabViewModel document)
    {
        var settingsIndex = IndexOfSettingsTab();
        if (settingsIndex < 0)
        {
            Tabs.Add(document);
        }
        else
        {
            Tabs.Insert(settingsIndex, document);
        }
    }

    private int IndexOfSettingsTab()
    {
        for (var index = 0; index < Tabs.Count; index++)
        {
            if (Tabs[index].IsSettingsTab)
            {
                return index;
            }
        }

        return -1;
    }

    private void EnsureSettingsTab(bool select)
    {
        if (SettingsTab is null)
        {
            SettingsTab = new SettingsTabViewModel(
                Options,
                CheckForUpdatesCommand,
                _dialogs.ShowErrorAsync,
                CloseTabCoreAsync);
            Tabs.Add(SettingsTab);
        }

        if (select)
        {
            SelectedTab = SettingsTab;
        }
    }

    private async Task CloseTabCoreAsync(WorkspaceTabViewModel tab)
    {
        if (tab is DocumentViewModel document)
        {
            if (document.IsModified && !await ResolveUnsavedAsync(document))
            {
                return;
            }

            RememberCaretPosition(document);
            document.PropertyChanged -= OnDocumentPropertyChanged;
        }
        else if (tab is PdfDocumentViewModel pdf)
        {
            pdf.Dispose();
        }
        else if (tab is SettingsTabViewModel)
        {
            SettingsTab = null;
        }

        var closingIndex = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        // 表示できるタブが 1 つも無い状態は作らない（設定タブだけになったら空文書を用意する）。
        if (!Tabs.Any(item => item is DocumentViewModel or PdfDocumentViewModel))
        {
            NewDocument();
            return;
        }

        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
        {
            var nextIndex = Math.Clamp(closingIndex, 0, Tabs.Count - 1);
            SelectedTab = Tabs[nextIndex];
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
            await _files.WriteAsync(path, document.CreateSaveContent(), Options.CreateBackupOnSave);
            document.MarkSaved(path);
            RememberCaretPosition(document);
            SelectedTab = document;
            StatusMessage = $"{document.DisplayName} を保存しました";
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.For<MainWindowViewModel>().Error($"ファイルを保存できませんでした: {path}", ex);
            await _dialogs.ShowErrorAsync(
                "ファイルを保存できません",
                $"{path}\n\n{ex.Message}");
            return false;
        }
    }

    /// <summary>終了時の確認（sakura の m_bExitConfirm）。設定が OFF ならビュー側から呼ばれない。</summary>
    public Task<bool> ConfirmExitAsync()
        => _dialogs.ConfirmAsync("Fumilume の終了", "Fumilume を終了しますか？");

    // ===== ファイル設定（sakura の共通設定『ファイル』相当） =====

    /// <summary>
    /// 大きなファイルを開く前に尋ねる（sakura の m_bAlertIfLargeFile / m_nAlertFileSize）。
    /// サイズが読めないときは黙って開く（存在しないファイルは後段のエラーで扱う）。
    /// </summary>
    private async Task<bool> ConfirmLargeFileAsync(string fullPath)
    {
        if (!Options.WarnOnLargeFile)
        {
            return true;
        }

        long length;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return true;
            }

            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }

        var thresholdBytes = (long)Options.LargeFileThresholdMegabytes * 1024 * 1024;
        if (length <= thresholdBytes)
        {
            return true;
        }

        return await _dialogs.ConfirmAsync(
            "大きなファイルを開きます",
            $"{Path.GetFileName(fullPath)} は {length / 1024.0 / 1024.0:N1} MB あります。\n開くまで時間がかかることがあります。続けますか？");
    }

    /// <summary>前回のカーソル位置へ戻す（sakura の m_bRestoreCurPosition）。</summary>
    private void RestoreCaretPosition(DocumentViewModel document, string fullPath)
    {
        if (!Options.RestoreCaretPosition ||
            !Options.Settings.CaretPositions.TryGetValue(fullPath, out var offset))
        {
            return;
        }

        document.CaretIndex = Math.Clamp(offset, 0, document.EditorDocument.TextLength);
    }

    /// <summary>閉じる・保存のたびにカーソル位置を控える。</summary>
    private void RememberCaretPosition(DocumentViewModel document)
    {
        if (!Options.RestoreCaretPosition || document.FilePath is not { } path)
        {
            return;
        }

        Options.Settings.CaretPositions[path] = document.CaretIndex;
        Options.Persist();
    }

    partial void OnSelectedTabChanged(WorkspaceTabViewModel? oldValue, WorkspaceTabViewModel? newValue)
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        NotifyDocumentCommandsChanged();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DocumentViewModel.IsModified))
        {
            SaveAllCommand.NotifyCanExecuteChanged();
        }

        if (!ReferenceEquals(sender, SelectedDocument))
        {
            return;
        }

        if (args.PropertyName is nameof(DocumentViewModel.DisplayTitle)
            or nameof(DocumentViewModel.TabTitle)
            or nameof(DocumentViewModel.FilePath))
        {
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(CurrentPath));
            ReloadCommand.NotifyCanExecuteChanged();
            ToggleMarkdownPreviewCommand.NotifyCanExecuteChanged();
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

    private void NotifyDocumentCommandsChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        SaveAllCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
        ToggleMarkdownPreviewCommand.NotifyCanExecuteChanged();
        GoToLineCommand.NotifyCanExecuteChanged();
    }

    private void RemovePristineInitialDocument()
    {
        if (Documents.Count() <= 1)
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
        Tabs.Remove(pristine);
    }
}
