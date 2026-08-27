using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fumilume.ViewModels;

/// <summary>
/// sakura エディタの「変換系」「編集系」「挿入系」コマンドを文書へ適用する面。
///
/// 変換規則そのものは <see cref="Services.TextTransforms"/> の純粋関数側にあり、ここは
/// 「どの範囲に当てるか」「Undo をどう束ねるか」「選択をどう残すか」だけを扱う。
/// 変換系は sakura と同じく選択範囲を対象にし、行系は選択が無ければカーソル行を対象にする。
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>選択範囲の先頭。ビューの <c>TextArea.Selection</c> と双方向で突き合わせる。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private int _selectionStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private int _selectionLength;

    public bool HasSelection => SelectionLength > 0;

    /// <summary>選択範囲の文字列。選択が無いときは空。</summary>
    public string SelectedText
    {
        get
        {
            var (offset, length) = ClampRange(SelectionStart, SelectionLength);
            return length == 0 ? string.Empty : EditorDocument.GetText(offset, length);
        }
    }

    // ===== 変換系 =====

    /// <summary>選択範囲へ変換を当てる。選択が無いときは何もせず <see langword="false"/> を返す。</summary>
    public bool TransformSelection(Func<string, string> transform)
    {
        var (offset, length) = ClampRange(SelectionStart, SelectionLength);
        if (length == 0)
        {
            return false;
        }

        ReplaceAndSelect(offset, length, transform(EditorDocument.GetText(offset, length)));
        return true;
    }

    /// <summary>
    /// 失敗しうる変換（Base64・URL デコード）を当てる。
    /// 変換関数が <see langword="null"/> を返したときは文書を触らない。
    /// </summary>
    public bool TryTransformSelection(Func<string, string?> transform)
    {
        var (offset, length) = ClampRange(SelectionStart, SelectionLength);
        if (length == 0)
        {
            return false;
        }

        var converted = transform(EditorDocument.GetText(offset, length));
        if (converted is null)
        {
            return false;
        }

        ReplaceAndSelect(offset, length, converted);
        return true;
    }

    // ===== 編集系（行） =====

    /// <summary>選択（無ければカーソル行）を行単位へ広げて変換を当てる。</summary>
    public void TransformSelectedLines(Func<string, string> transform)
    {
        var (offset, length) = GetLineRange();
        ReplaceAndSelect(offset, length, transform(EditorDocument.GetText(offset, length)));
    }

    /// <summary>対象行の直後に同じ内容をもう一度差し込む（sakura の F_DUPLICATELINE 相当）。</summary>
    public void DuplicateLines()
    {
        var (offset, length) = GetLineRange();
        var text = EditorDocument.GetText(offset, length);
        var newLine = GetDocumentNewLine();
        EditorDocument.Insert(offset + length, text.EndsWith(newLine, StringComparison.Ordinal)
            ? text
            : newLine + text);
        SetSelection(offset, length);
    }

    /// <summary>対象行を改行ごと消す（sakura の F_DELETE_LINE 相当）。</summary>
    public void DeleteLines()
    {
        // 対象行の決め方（選択が無ければカーソル行）は他の行コマンドと同じにする。
        var (offset, length) = GetLineRange();
        var lastLine = EditorDocument.GetLineByOffset(offset + length);
        var end = Math.Min(lastLine.Offset + lastLine.TotalLength, EditorDocument.TextLength);

        // 末尾行だけは後ろに改行が無いので、代わりに手前の改行を巻き込んで消す。
        if (end >= EditorDocument.TextLength &&
            EditorDocument.GetLineByOffset(offset).PreviousLine is { } previous)
        {
            offset = previous.Offset + previous.Length;
        }

        EditorDocument.Remove(offset, end - offset);
        SetSelection(Math.Min(offset, EditorDocument.TextLength), 0);
    }

    /// <summary>カーソル行を選択する（sakura の F_SELECTLINE 相当）。</summary>
    public void SelectCurrentLine()
    {
        var (offset, length) = GetLineRange();
        SetSelection(offset, length);
    }

    /// <summary>各行の先頭へ字下げを差し込む（sakura の F_INDENT_TAB / F_INDENT_SPACE 相当）。</summary>
    public void IndentLines(string indent)
        => TransformSelectedLines(text => MapLines(text, line => indent + line));

    /// <summary>各行の先頭から字下げを 1 段はがす（逆インデント）。</summary>
    public void UnindentLines(string indent)
        => TransformSelectedLines(text => MapLines(text, line => Unindent(line, indent)));

    /// <summary>カーソルから行頭までを消す（sakura の F_LineDeleteToStart 相当）。</summary>
    public void DeleteToLineStart()
    {
        var caret = ClampOffset(CaretIndex);
        var line = EditorDocument.GetLineByOffset(caret);
        if (caret > line.Offset)
        {
            EditorDocument.Remove(line.Offset, caret - line.Offset);
            SetSelection(line.Offset, 0);
        }
    }

    /// <summary>カーソルから行末までを消す（sakura の F_LineDeleteToEnd 相当）。改行は残す。</summary>
    public void DeleteToLineEnd()
    {
        var caret = ClampOffset(CaretIndex);
        var line = EditorDocument.GetLineByOffset(caret);
        var end = line.Offset + line.Length;
        if (end > caret)
        {
            EditorDocument.Remove(caret, end - caret);
            SetSelection(caret, 0);
        }
    }

    // ===== 挿入系 =====

    /// <summary>カーソル位置（選択があれば置き換えて）へ文字列を差し込む。</summary>
    public void InsertText(string text)
    {
        var (offset, length) = ClampRange(SelectionStart, SelectionLength);
        if (length == 0)
        {
            offset = ClampOffset(CaretIndex);
        }

        EditorDocument.Replace(offset, length, text);
        SetSelection(offset + text.Length, 0);
        CaretIndex = offset + text.Length;
    }

    // ===== ブックマーク =====

    private DocumentBookmarks? _bookmarks;

    /// <summary>行に付ける印。文書と同じ寿命で、タブを閉じるまで残る。</summary>
    public DocumentBookmarks Bookmarks => _bookmarks ??= new DocumentBookmarks(EditorDocument);

    /// <summary>カーソル行の印を反転する。付けたときは <see langword="true"/>。</summary>
    public bool ToggleBookmark() => Bookmarks.Toggle(CurrentLine);

    /// <summary>次の印へ飛ぶ。印が 1 つも無ければ <see langword="false"/>。</summary>
    public bool GoToNextBookmark() => GoToLineIfAny(Bookmarks.Next(CurrentLine));

    public bool GoToPreviousBookmark() => GoToLineIfAny(Bookmarks.Previous(CurrentLine));

    private bool GoToLineIfAny(int? lineNumber)
    {
        if (lineNumber is not { } line)
        {
            return false;
        }

        CaretIndex = GetLineStartOffset(line);
        SelectionStart = CaretIndex;
        SelectionLength = 0;
        return true;
    }

    // ===== 対括弧の検索 =====

    /// <summary>括弧の対。sakura の「対括弧の検索」と同じく、和文の括弧も対象にする。</summary>
    private const string OpenBrackets = "([{<「『（【〔｛［〈《";
    private const string CloseBrackets = ")]}>」』）】〕｝］〉》";

    /// <summary>
    /// カーソル位置の括弧に対応する括弧の位置を返す。括弧の上に居ないか、
    /// 対が見つからないときは <see langword="null"/>。
    /// </summary>
    public int? FindMatchingBracket()
    {
        var caret = ClampOffset(CaretIndex);
        var text = EditorDocument.Text;

        // カーソルの右側を先に見て、無ければ直前の文字を見る（閉じ括弧の直後で押したときに効かせる）。
        foreach (var position in new[] { caret, caret - 1 })
        {
            if (position < 0 || position >= text.Length)
            {
                continue;
            }

            var open = OpenBrackets.IndexOf(text[position]);
            if (open >= 0)
            {
                return Scan(text, position, text[position], CloseBrackets[open], step: 1);
            }

            var close = CloseBrackets.IndexOf(text[position]);
            if (close >= 0)
            {
                return Scan(text, position, text[position], OpenBrackets[close], step: -1);
            }
        }

        return null;
    }

    /// <summary>入れ子を数えながら対を探す。</summary>
    private static int? Scan(string text, int start, char from, char to, int step)
    {
        var depth = 0;
        for (var index = start; index >= 0 && index < text.Length; index += step)
        {
            if (text[index] == from)
            {
                depth++;
            }
            else if (text[index] == to && --depth == 0)
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>対括弧へカーソルを移す。移せたときは <see langword="true"/>。</summary>
    public bool GoToMatchingBracket()
    {
        if (FindMatchingBracket() is not { } position)
        {
            return false;
        }

        CaretIndex = position;
        SelectionStart = position;
        SelectionLength = 0;
        return true;
    }

    // ===== 内部 =====

    /// <summary>選択（無ければカーソル行）を、行頭から行末までへ広げた範囲。</summary>
    private (int Offset, int Length) GetLineRange()
    {
        var start = ClampOffset(SelectionStart);
        var end = ClampOffset(SelectionLength > 0 ? SelectionStart + SelectionLength : CaretIndex);
        if (SelectionLength == 0)
        {
            start = end = ClampOffset(CaretIndex);
        }

        var firstLine = EditorDocument.GetLineByOffset(Math.Min(start, end));
        // 選択が次行の頭ちょうどで終わっているときは、その行を巻き込まない。
        var lastOffset = Math.Max(start, end);
        if (lastOffset > firstLine.Offset && lastOffset == EditorDocument.GetLineByOffset(lastOffset).Offset)
        {
            lastOffset--;
        }

        var lastLine = EditorDocument.GetLineByOffset(lastOffset);
        return (firstLine.Offset, lastLine.Offset + lastLine.Length - firstLine.Offset);
    }

    private void ReplaceAndSelect(int offset, int length, string replacement)
    {
        if (string.Equals(EditorDocument.GetText(offset, length), replacement, StringComparison.Ordinal))
        {
            return;
        }

        EditorDocument.Replace(offset, length, replacement);
        SetSelection(offset, replacement.Length);
    }

    private void SetSelection(int offset, int length)
    {
        SelectionStart = ClampOffset(offset);
        SelectionLength = Math.Clamp(length, 0, EditorDocument.TextLength - SelectionStart);
        CaretIndex = SelectionStart + SelectionLength;
    }

    private int ClampOffset(int offset) => Math.Clamp(offset, 0, EditorDocument.TextLength);

    private (int Offset, int Length) ClampRange(int offset, int length)
    {
        var start = ClampOffset(offset);
        return (start, Math.Clamp(length, 0, EditorDocument.TextLength - start));
    }

    /// <summary>文書が実際に使っている改行。空文書のときは保存時の改行に合わせる。</summary>
    private string GetDocumentNewLine()
    {
        var first = EditorDocument.GetLineByNumber(1);
        return first.DelimiterLength switch
        {
            0 => NewLine,
            _ => EditorDocument.GetText(first.Offset + first.Length, first.DelimiterLength),
        };
    }

    /// <summary>末尾の改行を保ったまま、各行へ変換を当てる。</summary>
    private static string MapLines(string text, Func<string, string> transform)
        => string.Join('\n', text.Split('\n').Select(line =>
        {
            // Split('\n') の各要素には CR が残りうるので、変換対象からは外して付け直す。
            var hasCarriageReturn = line.EndsWith('\r');
            var body = hasCarriageReturn ? line[..^1] : line;
            return transform(body) + (hasCarriageReturn ? "\r" : string.Empty);
        }));

    private static string Unindent(string line, string indent)
    {
        if (line.StartsWith(indent, StringComparison.Ordinal))
        {
            return line[indent.Length..];
        }

        // TAB 1 個ぶんの字下げが空白で書かれている行も 1 段はがせるようにする。
        if (indent == "\t" && line.StartsWith(' '))
        {
            return line.TrimStart(' ');
        }

        return line.StartsWith(' ') || line.StartsWith('\t') ? line[1..] : line;
    }
}
