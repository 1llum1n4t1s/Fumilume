using System.ComponentModel;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using Fumilume.Models;
using Fumilume.Services;

namespace Fumilume.ViewModels;

public sealed partial class DocumentViewModel : WorkspaceTabViewModel
{
    private bool _isLoading;
    private bool _isMissingOnDisk;
    private string? _savedText = string.Empty;
    private DocumentEncoding _savedEncoding = DocumentEncoding.Utf8;
    private string _savedNewLine = Environment.NewLine;
    private string? _csvPreviewSource;
    private CsvDocument? _csvPreview;

    /// <summary>前回終了時の未保存内容から復元した文書か（保存するまで未保存のまま扱う）。</summary>
    private bool _restoredUnsaved;

    public DocumentViewModel(string untitledName, Func<WorkspaceTabViewModel, Task> closeAsync)
        : base(closeAsync)
    {
        UntitledName = untitledName;
        EditorDocument.TextChanged += OnEditorDocumentTextChanged;
        EditorDocument.UndoStack.PropertyChanged += OnUndoStackPropertyChanged;
        EditorDocument.UndoStack.MarkAsOriginalFile();
        UpdateTextStatistics();
    }

    public string UntitledName { get; }

    /// <summary>Segoe Fluent Icons の文書アイコン。</summary>
    public override string TabGlyph => "";

    public override string TabTitle => DisplayTitle;

    public override string TabTooltip => PathDisplay;

    public override bool IsDocumentTab => true;

    public TextDocument EditorDocument { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(PathDisplay))]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    [NotifyPropertyChangedFor(nameof(TabTooltip))]
    [NotifyPropertyChangedFor(nameof(IsMarkdown))]
    [NotifyPropertyChangedFor(nameof(IsCsv))]
    [NotifyPropertyChangedFor(nameof(CanShowPreview))]
    [NotifyPropertyChangedFor(nameof(PreviewFormatLabel))]
    private string? _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private bool _isModified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewToggleLabel))]
    private bool _isMarkdownPreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineColumnText))]
    private int _caretIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatisticsText))]
    private int _lineCount = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatisticsText))]
    private int _characterCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EncodingLabel))]
    private DocumentEncoding _encoding = DocumentEncoding.Utf8;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewLineLabel))]
    private string _newLine = Environment.NewLine;

    public string DisplayName => FilePath is null ? UntitledName : Path.GetFileName(FilePath);

    public string DisplayTitle => IsModified ? $"{DisplayName} ●" : DisplayName;

    public string PathDisplay => FilePath ?? "新しいテキスト文書";

    public bool IsMarkdown => string.Equals(Path.GetExtension(FilePath), ".md", StringComparison.OrdinalIgnoreCase);

    public bool IsCsv => string.Equals(Path.GetExtension(FilePath), ".csv", StringComparison.OrdinalIgnoreCase);

    public bool CanShowPreview => IsMarkdown || IsCsv;

    public string PreviewFormatLabel => IsCsv ? "CSV" : "MD";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewToggleLabel))]
    private bool _isCsvPreview;

    public bool IsEditorVisible => !IsMarkdownPreview && !IsCsvPreview;

    public string PreviewToggleLabel => !IsEditorVisible ? "編集へ戻る" : "プレビュー";

    public string EncodingLabel => Encoding switch
    {
        DocumentEncoding.Utf8 => "UTF-8",
        DocumentEncoding.Utf8Bom => "UTF-8 BOM",
        DocumentEncoding.Utf16LittleEndian => "UTF-16 LE",
        DocumentEncoding.Utf16BigEndian => "UTF-16 BE",
        DocumentEncoding.Utf16LittleEndianNoBom => "UTF-16 LE (BOMなし)",
        DocumentEncoding.Utf16BigEndianNoBom => "UTF-16 BE (BOMなし)",
        DocumentEncoding.Utf32LittleEndian => "UTF-32 LE",
        DocumentEncoding.Utf32BigEndian => "UTF-32 BE",
        DocumentEncoding.Utf32LittleEndianNoBom => "UTF-32 LE (BOMなし)",
        DocumentEncoding.Utf32BigEndianNoBom => "UTF-32 BE (BOMなし)",
        DocumentEncoding.ShiftJis => "Shift_JIS",
        DocumentEncoding.EucJp => "EUC-JP",
        DocumentEncoding.Iso2022Jp => "ISO-2022-JP",
        _ => "UTF-8",
    };

    public string NewLineLabel => NewLine switch
    {
        "\r\n" => "CRLF",
        "\n" => "LF",
        "\r" => "CR",
        _ => "改行",
    };

    public string StatisticsText => $"{LineCount:N0} 行  |  {CharacterCount:N0} 文字";

    public string Text
    {
        get => EditorDocument.Text;
        set
        {
            value ??= string.Empty;
            if (!string.Equals(EditorDocument.Text, value, StringComparison.Ordinal))
            {
                EditorDocument.Text = value;
            }
        }
    }

    public bool CanUndo => EditorDocument.UndoStack.CanUndo;

    public bool CanRedo => EditorDocument.UndoStack.CanRedo;

    public int CurrentLine
    {
        get
        {
            var safeIndex = Math.Clamp(CaretIndex, 0, EditorDocument.TextLength);
            return EditorDocument.GetLineByOffset(safeIndex).LineNumber;
        }
    }

    public string LineColumnText
    {
        get
        {
            var safeIndex = Math.Clamp(CaretIndex, 0, EditorDocument.TextLength);
            var line = EditorDocument.GetLineByOffset(safeIndex);
            var column = safeIndex - line.Offset + 1;
            return $"行 {line.LineNumber:N0}、列 {column:N0}";
        }
    }

    public TextDocumentContent CreateSaveContent()
        => new(Text, Encoding, NewLine);

    public void Load(string path, TextDocumentContent content)
    {
        _isLoading = true;
        try
        {
            FilePath = Path.GetFullPath(path);
            Encoding = content.Encoding;
            NewLine = content.NewLine;
            _savedEncoding = Encoding;
            _savedNewLine = NewLine;
            _savedText = content.Text;
            Text = content.Text;
            CaretIndex = 0;
            _restoredUnsaved = false;
            _isMissingOnDisk = false;
            IsModified = false;
        }
        finally
        {
            _isLoading = false;
            EditorDocument.UndoStack.ClearAll();
            EditorDocument.UndoStack.MarkAsOriginalFile();
            UpdateTextStatistics();
        }
    }

    /// <summary>
    /// 前回終了時の未保存の内容を、ディスクを読まずに復元する。
    ///
    /// 復元直後は Undo 履歴が空なので「Undo し切ったから保存済みへ戻る」とは言えない。
    /// <see cref="_restoredUnsaved"/> を立てて、保存するまで未保存のままにしておく。
    /// </summary>
    public void RestoreUnsaved(string? path, TextDocumentContent content)
    {
        _isLoading = true;
        try
        {
            FilePath = path is null ? null : Path.GetFullPath(path);
            Encoding = content.Encoding;
            NewLine = content.NewLine;
            Text = content.Text;
            CaretIndex = 0;
        }
        finally
        {
            _isLoading = false;
            EditorDocument.UndoStack.ClearAll();
            UpdateTextStatistics();
            _savedText = null;
            _restoredUnsaved = true;
            IsModified = true;
        }
    }

    public void MarkSaved(string path)
        => MarkSaved(path, CreateSaveContent(), EditorDocument.Version);

    /// <summary>実際に書き込んだスナップショットだけを保存基準にし、保存待ちの追加入力は未保存として残す。</summary>
    internal void MarkSaved(string path, TextDocumentContent savedContent, object savedVersion)
    {
        var unchangedDuringSave = ReferenceEquals(EditorDocument.Version, savedVersion)
            && Encoding == savedContent.Encoding
            && string.Equals(NewLine, savedContent.NewLine, StringComparison.Ordinal);
        FilePath = Path.GetFullPath(path);
        _savedEncoding = savedContent.Encoding;
        _savedNewLine = savedContent.NewLine;
        _savedText = DocumentFileService.NormalizeNewLines(savedContent.Text, savedContent.NewLine);
        _isMissingOnDisk = false;
        if (unchangedDuringSave)
        {
            EditorDocument.UndoStack.MarkAsOriginalFile();
        }

        _restoredUnsaved = !unchangedDuringSave;
        IsModified = !unchangedDuringSave;
    }

    /// <summary>監視通知が自分自身の保存に由来する場合、ディスク内容は保存時の基準値と一致する。</summary>
    internal bool HasSavedContentBaseline => _savedText is not null;

    internal bool MatchesSavedContent(TextDocumentContent content)
        => _savedText is not null
            && content.Encoding == _savedEncoding
            && string.Equals(content.Text, _savedText, StringComparison.Ordinal);

    /// <summary>外部削除後の本文を、閉じる確認とセッション保存の対象として保持する。</summary>
    internal void MarkMissingOnDisk()
    {
        _isMissingOnDisk = true;
        IsModified = true;
    }

    /// <summary>保存時の内容が再び存在すると確認できたら、ディスク消失状態だけを解消する。</summary>
    internal void MarkSavedContentPresent()
    {
        _isMissingOnDisk = false;
        UpdateModifiedState();
    }

    /// <summary>未保存セッションでは本文を変えず、現在のディスク内容だけを今後の比較基準にする。</summary>
    internal void RememberSavedContentBaseline(TextDocumentContent content)
    {
        _savedText = content.Text;
        _savedEncoding = content.Encoding;
        _savedNewLine = content.NewLine;
        _isMissingOnDisk = false;
        UpdateModifiedState();
    }

    public void TogglePreview()
    {
        if (IsMarkdown)
        {
            IsMarkdownPreview = !IsMarkdownPreview;
        }
        else if (IsCsv)
        {
            IsCsvPreview = !IsCsvPreview;
        }
    }

    /// <summary>
    /// CSV プレビューで確定したセル値を、表示時の本文が変わっていない場合だけ元テキストへ反映する。
    /// 区切りと行末を保つため、CSV 全体を作り直さず対象フィールドの範囲だけを置き換える。
    /// </summary>
    public bool TryUpdateCsvCell(
        string expectedSource,
        int rowIndex,
        int columnIndex,
        string value)
    {
        if (!IsCsv
            || rowIndex < 0
            || rowIndex >= CsvDocumentParser.MaxPreviewRows
            || columnIndex < 0
            || columnIndex >= CsvDocumentParser.MaxPreviewColumns
            || !string.Equals(Text, expectedSource, StringComparison.Ordinal))
        {
            return false;
        }

        var csv = GetCsvPreview(expectedSource);
        if (csv.HasUnterminatedQuotedField || rowIndex >= csv.Rows.Count
            || columnIndex >= csv.DisplayedColumnCount)
        {
            return false;
        }

        value ??= string.Empty;
        var currentValue = columnIndex < csv.Rows[rowIndex].Count
            ? csv.Rows[rowIndex][columnIndex]
            : string.Empty;
        if (string.Equals(currentValue, value, StringComparison.Ordinal))
        {
            return true;
        }

        var normalizedValue = DocumentFileService.NormalizeNewLines(value, NewLine);
        if (string.Equals(currentValue, normalizedValue, StringComparison.Ordinal))
        {
            return true;
        }

        var ranges = csv.CellSourceRanges[rowIndex];
        var replacement = CsvTableEditingService.EncodeCsvField(normalizedValue);
        if (columnIndex < ranges.Count)
        {
            var range = ranges[columnIndex];
            EditorDocument.Replace(range.Offset, range.Length, replacement);
        }
        else
        {
            // 不揃い行の欠落セルは、この行の最後のフィールドの直後に補完する。
            var last = ranges[^1];
            EditorDocument.Insert(last.Offset + last.Length,
                new string(',', columnIndex - ranges.Count + 1) + replacement);
        }

        return true;
    }

    internal bool TryGetCsvPreview(string source, out CsvDocument parsed)
    {
        parsed = _csvPreview!;
        return parsed is not null && string.Equals(_csvPreviewSource, source, StringComparison.Ordinal);
    }

    internal void CacheCsvPreview(string source, CsvDocument parsed)
    {
        if (string.Equals(Text, source, StringComparison.Ordinal))
        {
            _csvPreviewSource = source;
            _csvPreview = parsed;
        }
    }

    internal CsvDocument GetCsvPreview(string source)
    {
        if (TryGetCsvPreview(source, out var parsed))
        {
            return parsed;
        }
        parsed = CsvDocumentParser.Parse(source);
        CacheCsvPreview(source, parsed);
        return parsed;
    }

    partial void OnFilePathChanged(string? value)
    {
        if (!IsCsv)
        {
            IsCsvPreview = false;
        }
        if (!IsMarkdown)
        {
            IsMarkdownPreview = false;
        }
    }

    partial void OnEncodingChanged(DocumentEncoding value)
    {
        if (!_isLoading)
        {
            UpdateModifiedState();
        }
    }

    partial void OnNewLineChanged(string value)
    {
        if (!_isLoading)
        {
            UpdateModifiedState();
        }
    }

    public int GetLineStartOffset(int lineNumber)
        => EditorDocument.GetLineByNumber(Math.Clamp(lineNumber, 1, EditorDocument.LineCount)).Offset;

    private void OnEditorDocumentTextChanged(object? sender, EventArgs args)
    {
        _csvPreviewSource = null;
        _csvPreview = null;
        OnPropertyChanged(nameof(Text));
        UpdateTextStatistics();
        OnPropertyChanged(nameof(LineColumnText));
        OnPropertyChanged(nameof(CurrentLine));
        if (!_isLoading)
        {
            IsModified = true;
        }
    }

    partial void OnCaretIndexChanged(int value)
    {
        OnPropertyChanged(nameof(LineColumnText));
        OnPropertyChanged(nameof(CurrentLine));
    }

    private void OnUndoStackPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(UndoStack.CanUndo) or nameof(UndoStack.IsOriginalFile))
        {
            OnPropertyChanged(nameof(CanUndo));
        }

        if (args.PropertyName == nameof(UndoStack.CanRedo))
        {
            OnPropertyChanged(nameof(CanRedo));
        }

        if (!_isLoading && args.PropertyName == nameof(UndoStack.IsOriginalFile))
        {
            UpdateModifiedState();
        }
    }

    private void UpdateModifiedState()
        => IsModified = _restoredUnsaved || _isMissingOnDisk || !EditorDocument.UndoStack.IsOriginalFile
            || Encoding != _savedEncoding || NewLine != _savedNewLine;

    private void UpdateTextStatistics()
    {
        CharacterCount = Text.Length;
        // AvaloniaEdit は LF / CRLF / CR のどれも改行として扱う。文字だけを数えると
        // 古い Mac 形式（CR）の文書で、表示中の行数とステータスバーが食い違う。
        LineCount = EditorDocument.LineCount;
    }
}
