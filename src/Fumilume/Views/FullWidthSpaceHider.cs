using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Fumilume.Views;

/// <summary>
/// 全角空白（U+3000）に可視グリフを持つフォント向けに、前景を透明にして記号を隠す。
/// 本文と文字幅は変えない。構文ハイライトや選択範囲の前景指定より後に適用する必要がある。
/// </summary>
internal sealed class FullWidthSpaceHider : DocumentColorizingTransformer
{
    private const char FullWidthSpace = '　';

    public bool IsEnabled { get; set; }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!IsEnabled)
        {
            return;
        }

        var text = CurrentContext.Document.GetText(line);
        for (var index = text.IndexOf(FullWidthSpace);
             index >= 0;
             index = text.IndexOf(FullWidthSpace, index + 1))
        {
            var offset = line.Offset + index;
            ChangeLinePart(offset, offset + 1, element =>
                element.TextRunProperties.SetForegroundBrush(Brushes.Transparent));
        }
    }
}
