using System.ComponentModel;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using Fumilume.Models;

namespace Fumilume.ViewModels;

public sealed partial class DocumentViewModel : WorkspaceTabViewModel
{
    private bool _isLoading;

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

    public TextDocument EditorDocument { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(PathDisplay))]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    [NotifyPropertyChangedFor(nameof(TabTooltip))]
    private string? _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(TabTitle))]
    private bool _isModified;

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

    public string EncodingLabel => Encoding switch
    {
        DocumentEncoding.Utf8 => "UTF-8",
        DocumentEncoding.Utf8Bom => "UTF-8 BOM",
        DocumentEncoding.Utf16LittleEndian => "UTF-16 LE",
        DocumentEncoding.Utf16BigEndian => "UTF-16 BE",
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
            Text = content.Text;
            CaretIndex = 0;
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

    public void MarkSaved(string path)
    {
        FilePath = Path.GetFullPath(path);
        EditorDocument.UndoStack.MarkAsOriginalFile();
        IsModified = false;
    }

    public int GetLineStartOffset(int lineNumber)
        => EditorDocument.GetLineByNumber(Math.Clamp(lineNumber, 1, EditorDocument.LineCount)).Offset;

    private void OnEditorDocumentTextChanged(object? sender, EventArgs args)
    {
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
            IsModified = !EditorDocument.UndoStack.IsOriginalFile;
        }
    }

    private void UpdateTextStatistics()
    {
        CharacterCount = Text.Length;
        LineCount = Text.Length == 0 ? 1 : Text.Count(character => character == '\n') + 1;
    }
}
