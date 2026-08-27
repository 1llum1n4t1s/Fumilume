using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fumilume.Services;

namespace Fumilume.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>2 枚目以降の未保存文書に付く名前の前置き（採番の読み書きで共有する）。</summary>
    private const string UntitledPrefix = "無題 ";

    private readonly IDocumentFileService _files;
    private readonly IEditorDialogService _dialogs;
    private readonly IGrepService _grep;
    private int _untitledSequence;

    /// <summary>起動時の復元処理。終了時はこれの完了を待ってからセッションを書き直す。</summary>
    private Task? _initialization;

    public MainWindowViewModel(
        IDocumentFileService files,
        IEditorDialogService dialogs,
        AppSettings? settings = null,
        IGrepService? grep = null)
    {
        _files = files;
        _dialogs = dialogs;
        // 検索は読み込みの経路を文書と揃えたいので、既定では同じファイルサービスの上に組む。
        _grep = grep ?? new GrepService(files);
        Options = new AppOptionsViewModel(settings ?? new AppSettings());

        // 生成プロパティ経由で入れると設定へ書き戻しが走るため、ここは補助フィールドへ直接入れる。
        _sidePanel = Options.SidePanel;
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
    [NotifyPropertyChangedFor(nameof(SelectedGrep))]
    [NotifyPropertyChangedFor(nameof(IsGrepSelected))]
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

    public GrepResultTabViewModel? SelectedGrep => SelectedTab as GrepResultTabViewModel;

    public bool IsGrepSelected => SelectedTab is GrepResultTabViewModel;

    /// <summary>開いている文書だけを取り出す（未保存確認や重複オープンの判定に使う）。</summary>
    public IEnumerable<DocumentViewModel> Documents => Tabs.OfType<DocumentViewModel>();

    public bool CanUndo => SelectedDocument?.CanUndo == true;

    public bool CanRedo => SelectedDocument?.CanRedo == true;

    public string WindowTitle => $"{SelectedTab?.TabTitle ?? "Fumilume"} - Fumilume";

    public string CurrentPath => SelectedDocument?.PathDisplay
        ?? SelectedPdf?.FilePath
        ?? SelectedGrep?.Query.Describe()
        ?? "新しいテキスト文書";

    /// <summary>起動時の復元と引数のオープン。戻り値は <see cref="PersistSessionStateAsync"/> が待つ。</summary>
    public Task InitializeAsync(IEnumerable<string> startupArgs)
        => _initialization = InitializeCoreAsync(startupArgs);

    private async Task InitializeCoreAsync(IEnumerable<string> startupArgs)
    {
        // 前回終了時のタブを先に戻し、起動引数のファイルはその上へ開く（引数のタブが前面に来る）。
        var session = Options.RestoreSession ? SessionStateService.Load() : new SessionState();
        await RestoreSessionAsync(session);

        var paths = startupArgs.Where(File.Exists).ToArray();
        if (paths.Length > 0)
        {
            await OpenPathsAsync(paths);
        }

        // 前回終了時に設定タブを開いていたなら同じ状態へ戻す（選択は文書のまま）。
        if (session.SettingsTabOpen)
        {
            EnsureSettingsTab(select: false);
        }
    }

    public async Task<bool> CanCloseAsync()
    {
        // タブを復元する設定なら、未保存でもそのまま閉じられる（内容は次回起動時に戻る）。
        // 個別のタブを閉じるときは今までどおり確認する（そのタブは復元対象から外れるため）。
        if (Options.RestoreSession)
        {
            return true;
        }

        foreach (var document in Documents.Where(item => item.IsModified).ToArray())
        {
            if (!await ResolveUnsavedAsync(document))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 終了時に、次回復元したい状態を書き戻す。
    ///
    /// 未保存の文書は「確認せずに閉じる代わりにセッションへ預ける」約束なので、預け先へ書けなかった
    /// ときは終了させない。書けたか（＝閉じてよいか）を戻り値で返す。
    /// </summary>
    public async Task<bool> PersistSessionStateAsync()
    {
        // 復元の途中で書くと、まだ戻していないタブの控えを「使われていない」と判断して消してしまう。
        await WaitForInitializationAsync();

        foreach (var document in Documents)
        {
            RememberCaretPosition(document);
        }

        Options.Persist();

        if (!Options.RestoreSession)
        {
            // 復元しない設定へ切り替えたあとに古い控えが残らないようにする。
            SessionStateService.Clear();
            return true;
        }

        if (SessionStateService.Save(CaptureSession()))
        {
            return true;
        }

        // 失われるものが無ければ、保存できなくても終了は妨げない。
        if (!Documents.Any(document => document.IsModified))
        {
            return true;
        }

        await _dialogs.ShowErrorAsync(
            "未保存の内容を引き継げません",
            $"作業中の内容を {AppStoragePaths.Directory} へ控えられませんでした。\n\n"
            + "空き容量とアクセス権を確認するか、必要な文書を保存してから終了してください。");
        return false;
    }

    /// <summary>起動時の復元が終わるのを待つ。失敗していても終了処理は続ける。</summary>
    private async Task WaitForInitializationAsync()
    {
        if (_initialization is not { } initialization)
        {
            return;
        }

        try
        {
            await initialization;
        }
        catch (Exception ex)
        {
            AppLogger.For<MainWindowViewModel>().Error("起動時の復元が失敗したまま終了処理へ入りました。", ex);
        }
    }

    [RelayCommand]
    private void NewDocument()
    {
        _untitledSequence++;
        var document = new DocumentViewModel(UntitledNameFor(_untitledSequence), CloseTabCoreAsync);
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

    /// <summary>フォルダを横断して探す（秀丸の grep 相当）。結果は専用のタブへ並べる。</summary>
    [RelayCommand]
    private async Task GrepAsync()
    {
        var query = await _dialogs.PickGrepQueryAsync(CreateInitialGrepQuery());
        if (query is null)
        {
            return;
        }

        // 次に開くときの初期値として、条件をそのまま覚えておく。
        Options.RememberGrepQuery(query);

        var tab = new GrepResultTabViewModel(query, _grep, OpenGrepMatchAsync, CloseTabCoreAsync);
        InsertContentTab(tab);
        SelectedTab = tab;
        await tab.RunAsync();
        StatusMessage = tab.Status;
    }

    /// <summary>探す文字列は選択中の語、探す場所は今の文書のフォルダを初期値にする。</summary>
    private GrepQuery CreateInitialGrepQuery()
    {
        var settings = Options.Settings;
        var pattern = SelectedDocument?.SelectedText is { Length: > 0 } selected && !selected.Contains('\n')
            ? selected
            : settings.GrepPattern;
        var folder = SelectedDocument?.FilePath is { } path
            ? Path.GetDirectoryName(path) ?? settings.GrepFolder
            : settings.GrepFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        return new GrepQuery(
            pattern,
            folder,
            settings.GrepFileMask,
            settings.GrepIncludeSubfolders,
            Options.SearchMatchCase,
            Options.SearchUseRegex);
    }

    /// <summary>検索結果の 1 行からファイルを開き、その行を選択して見せる。</summary>
    private async Task OpenGrepMatchAsync(GrepMatch match)
    {
        await OpenPathsAsync([match.FilePath]);
        if (SelectedDocument is not { } document)
        {
            return;
        }

        var editorDocument = document.EditorDocument;
        var lineNumber = Math.Clamp(match.LineNumber, 1, editorDocument.LineCount);
        var line = editorDocument.GetLineByNumber(lineNumber);

        // 一致した桁へカーソルを置き、行全体を選択して目で追えるようにする。
        document.CaretIndex = Math.Clamp(line.Offset + match.Column - 1, line.Offset, line.EndOffset);
        document.SelectionStart = line.Offset;
        document.SelectionLength = line.Length;
        StatusMessage = $"{Path.GetFileName(match.FilePath)} の {lineNumber:N0} 行目へ移動しました";
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
        else if (tab is GrepResultTabViewModel grep)
        {
            // 検索中に閉じられたら、走っている検索も止める。
            grep.Dispose();
        }
        else if (tab is SettingsTabViewModel)
        {
            SettingsTab = null;
        }

        var closingIndex = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        // 表示できるタブが 1 つも無い状態は作らない（設定タブだけになったら空文書を用意する）。
        if (!Tabs.Any(item => item is DocumentViewModel or PdfDocumentViewModel or GrepResultTabViewModel))
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
        OnSelectedTabChangedForSidePanel();
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

        if (args.PropertyName == nameof(DocumentViewModel.Text))
        {
            OnSelectedTextChangedForSidePanel();
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

        var pristine = Documents.FirstOrDefault(IsPristine);
        if (pristine is null)
        {
            return;
        }

        DetachDocument(pristine);
        Tabs.Remove(pristine);
    }

    /// <summary>一度も触られていない空の新規文書か（開いた文書に押し出してよい相手）。</summary>
    private static bool IsPristine(DocumentViewModel document)
        => document.FilePath is null && !document.IsModified && document.Text.Length == 0;

    /// <summary>一覧から外す文書の購読を解く。</summary>
    private void DetachDocument(DocumentViewModel document)
        => document.PropertyChanged -= OnDocumentPropertyChanged;

    private static string UntitledNameFor(int sequence)
        => sequence <= 1 ? "無題" : UntitledPrefix + sequence.ToString();
}
