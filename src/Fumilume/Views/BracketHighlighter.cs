using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Avalonia.Threading;
using Fumilume.Services;

namespace Fumilume.Views;

/// <summary>括弧の深さ別前景色と、カーソル脇の対応ペアをエディタへ描く。</summary>
internal sealed class BracketHighlighter
{
    private static readonly Color[] DarkColors =
    [
        Color.Parse("#C678DD"),
        Color.Parse("#61AFEF"),
        Color.Parse("#E5C07B"),
        Color.Parse("#E06C75"),
        Color.Parse("#56B6C2"),
        Color.Parse("#98C379"),
    ];

    private static readonly Color[] LightColors =
    [
        Color.Parse("#A626A4"),
        Color.Parse("#4078F2"),
        Color.Parse("#C18401"),
        Color.Parse("#E45649"),
        Color.Parse("#0184BC"),
        Color.Parse("#50A14F"),
    ];

    private readonly TextEditor _editor;
    private readonly BracketColorizer _colorizer;
    private readonly BracketPairRenderer _renderer;
    private string? _filePath;
    private bool _enabled;
    private TextDocument? _cachedDocument;
    private object? _cachedVersion;
    private BracketLanguage _cachedLanguage;
    private BracketAnalysis _cachedAnalysis = BracketAnalysis.Empty;
    private bool _redrawPending;

    public BracketHighlighter(TextEditor editor)
    {
        _editor = editor;
        _colorizer = new BracketColorizer(this);
        _renderer = new BracketPairRenderer(editor, this);
        editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        editor.TextChanged += OnEditorTextChanged;
    }

    public void Apply(string? filePath, bool enabled)
    {
        _filePath = filePath;
        _enabled = enabled;
        ClearCache();

        // 構文ハイライト本体は先頭へ挿入される。深さ別配色を最後に適用するため末尾を維持する。
        var transformers = _editor.TextArea.TextView.LineTransformers;
        transformers.Remove(_colorizer);
        transformers.Add(_colorizer);
        _editor.TextArea.TextView.Redraw();
        InvalidatePairHighlight();
    }

    public void InvalidatePairHighlight()
        => _editor.TextArea.TextView.InvalidateLayer(_renderer.Layer);

    private void OnEditorTextChanged(object? sender, EventArgs args)
    {
        ClearCache();
        if (!_enabled
            || BracketPairService.LanguageFor(_filePath) == BracketLanguage.None
            || _redrawPending)
        {
            return;
        }

        // 先頭側の括弧が変わると後続行すべての深さが変わりうる。AvaloniaEdit の部分再描画だけでは
        // 画面内の後続行が古い色を保つため、文書更新が終わった描画優先度で 1 回にまとめて引き直す。
        _redrawPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _redrawPending = false;
            _editor.TextArea.TextView.Redraw();
            InvalidatePairHighlight();
        }, DispatcherPriority.Render);
    }

    private void ClearCache()
    {
        _cachedDocument = null;
        _cachedVersion = null;
        _cachedLanguage = BracketLanguage.None;
        _cachedAnalysis = BracketAnalysis.Empty;
    }

    private BracketAnalysis AnalysisFor(TextDocument document)
    {
        var language = _enabled ? BracketPairService.LanguageFor(_filePath) : BracketLanguage.None;
        var version = document.Version;
        if (ReferenceEquals(document, _cachedDocument)
            && ReferenceEquals(version, _cachedVersion)
            && language == _cachedLanguage)
        {
            return _cachedAnalysis;
        }

        _cachedDocument = document;
        _cachedVersion = version;
        _cachedLanguage = language;
        _cachedAnalysis = BracketPairService.Analyze(document.Text, language);
        return _cachedAnalysis;
    }

    private IBrush BrushForDepth(int depth)
    {
        var colors = _editor.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? DarkColors
            : LightColors;
        return new SolidColorBrush(colors[depth % colors.Length]);
    }

    private sealed class BracketColorizer(BracketHighlighter owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            foreach (var token in owner.AnalysisFor(CurrentContext.Document).InRange(line.Offset, line.EndOffset))
            {
                var brush = owner.BrushForDepth(token.Depth);
                ChangeLinePart(token.Offset, token.Offset + 1, element =>
                    element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }

    private sealed class BracketPairRenderer(TextEditor editor, BracketHighlighter owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid
                || !owner.AnalysisFor(textView.Document).TryGetPairBesideCaret(
                    editor.TextArea.Caret.Offset,
                    out var token,
                    out var pair)
                || FindBrush(textView, "BracketMatchBg") is not { } background
                || FindBrush(textView, "BracketMatchBorder") is not { } border)
            {
                return;
            }

            var geometry = new BackgroundGeometryBuilder
            {
                AlignToWholePixels = true,
                BorderThickness = 1,
                CornerRadius = 2,
            };
            geometry.AddSegment(textView, new SimpleSegment(token.Offset, 1));
            geometry.AddSegment(textView, new SimpleSegment(pair.Offset, 1));
            if (geometry.CreateGeometry() is { } pairGeometry)
            {
                drawingContext.DrawGeometry(background, new Pen(border, 1), pairGeometry);
            }
        }

        private static IBrush? FindBrush(TextView textView, string key)
            => textView.TryFindResource(key, textView.ActualThemeVariant, out var value)
                ? value as IBrush
                : null;
    }
}
