using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using Fumilume.ViewModels;

namespace Fumilume.Views;

/// <summary>
/// ブックマークの付いた行に目印を描く。
///
/// sakura は行番号の左に専用のマージンを立てて印を出すが、Fumilume は行番号マージンの
/// 見た目を変えたくないので、背景レイヤーへ「行全体の薄い地＋左端のアクセント帯」を敷く。
/// 折り返し中の行でも先頭の 1 本だけを描くので、印の数と帯の数が合う。
/// </summary>
internal sealed class BookmarkRenderer(Func<DocumentViewModel?> getDocument) : IBackgroundRenderer
{
    /// <summary>選択やカーソル行の強調より下に敷く（それらを隠さない）。</summary>
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (getDocument() is not { } document || textView.VisualLinesValid is false)
        {
            return;
        }

        var lines = document.Bookmarks.Lines;
        if (lines.Count == 0)
        {
            return;
        }

        // 色はテーマ辞書から都度引く（ライト／ダークの切り替えに追随させるため）。
        if (FindBrush(textView, "BookmarkTint") is not { } tint ||
            FindBrush(textView, "AccentBrush") is not { } accent)
        {
            return;
        }

        foreach (var visualLine in textView.VisualLines)
        {
            if (!lines.Contains(visualLine.FirstDocumentLine.LineNumber))
            {
                continue;
            }

            var top = visualLine.VisualTop - textView.VerticalOffset;
            var height = visualLine.Height;
            drawingContext.FillRectangle(tint, new Rect(0, top, textView.Bounds.Width, height));
            drawingContext.FillRectangle(accent, new Rect(0, top, 3, height));
        }
    }

    private static IBrush? FindBrush(TextView textView, string key)
        => textView.TryFindResource(key, textView.ActualThemeVariant, out var value)
            ? value as IBrush
            : null;
}
