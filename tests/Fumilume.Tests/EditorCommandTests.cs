using Fumilume.Services;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

/// <summary>
/// sakura の変換系・編集系コマンドを文書へ当てる面の検証。
/// 変換規則そのものは <see cref="TextTransformTests"/> 側で見ているので、ここでは
/// 「どの範囲に当たるか」「選択がどう残るか」「Undo で戻るか」を確かめる。
/// </summary>
public sealed class EditorCommandTests
{
    [Fact]
    public void ConversionAppliesToTheSelectionAndKeepsItSelected()
    {
        var document = CreateDocument("abc def");
        document.SelectionStart = 0;
        document.SelectionLength = 3;

        Assert.True(document.TransformSelection(TextTransforms.ToUpper));

        Assert.Equal("ABC def", document.Text);
        Assert.Equal(0, document.SelectionStart);
        Assert.Equal(3, document.SelectionLength);
    }

    /// <summary>sakura と同じく、変換系は選択が無いと何もしない（文書を勝手に書き換えない）。</summary>
    [Fact]
    public void ConversionWithoutSelectionChangesNothing()
    {
        var document = CreateDocument("abc");

        Assert.False(document.TransformSelection(TextTransforms.ToUpper));
        Assert.Equal("abc", document.Text);
    }

    /// <summary>デコードできない文字列では文書を触らない。</summary>
    [Fact]
    public void FailedDecodeLeavesTheDocumentAlone()
    {
        var document = CreateDocument("!!!not base64!!!");
        document.SelectionStart = 0;
        document.SelectionLength = document.Text.Length;

        Assert.False(document.TryTransformSelection(TextTransforms.Base64Decode));
        Assert.Equal("!!!not base64!!!", document.Text);
    }

    /// <summary>行系は選択が無ければカーソル行が対象になる。</summary>
    [Fact]
    public void LineCommandsFallBackToTheCaretLine()
    {
        var document = CreateDocument("one\ntwo\nthree");
        document.CaretIndex = 5; // 2 行目

        document.TransformSelectedLines(TextTransforms.ToUpper);

        Assert.Equal("one\nTWO\nthree", document.Text);
    }

    [Fact]
    public void DuplicateInsertsACopyBelow()
    {
        var document = CreateDocument("one\ntwo");
        document.CaretIndex = 0;

        document.DuplicateLines();

        Assert.Equal("one\none\ntwo", document.Text);
    }

    [Fact]
    public void DeleteLineRemovesTheLineWithItsNewLine()
    {
        var document = CreateDocument("one\ntwo\nthree");
        document.CaretIndex = 4; // 2 行目の先頭

        document.DeleteLines();

        Assert.Equal("one\nthree", document.Text);
    }

    /// <summary>末尾行は後ろに改行が無いので、手前の改行を巻き込んで消す。</summary>
    [Fact]
    public void DeletingTheLastLineDoesNotLeaveAnEmptyLine()
    {
        var document = CreateDocument("one\ntwo");
        document.CaretIndex = 4;

        document.DeleteLines();

        Assert.Equal("one", document.Text);
    }

    [Fact]
    public void IndentAndUnindentAreSymmetric()
    {
        var document = CreateDocument("a\nb");
        document.SelectionStart = 0;
        document.SelectionLength = document.Text.Length;

        document.IndentLines("\t");
        Assert.Equal("\ta\n\tb", document.Text);

        document.UnindentLines("\t");
        Assert.Equal("a\nb", document.Text);
    }

    [Fact]
    public void DeleteToLineEdgesKeepsTheNewLine()
    {
        var document = CreateDocument("hello world\nnext");
        document.CaretIndex = 5;
        document.DeleteToLineEnd();
        Assert.Equal("hello\nnext", document.Text);

        document.CaretIndex = 5;
        document.DeleteToLineStart();
        Assert.Equal("\nnext", document.Text);
    }

    [Fact]
    public void InsertReplacesTheSelection()
    {
        var document = CreateDocument("abc");
        document.SelectionStart = 1;
        document.SelectionLength = 1;

        document.InsertText("XY");

        Assert.Equal("aXYc", document.Text);
        Assert.Equal(3, document.CaretIndex);
    }

    /// <summary>コマンドは文書の Undo へ 1 手として積まれる。</summary>
    [Fact]
    public void CommandsAreUndoable()
    {
        var document = CreateDocument("abc");
        document.SelectionStart = 0;
        document.SelectionLength = 3;

        document.TransformSelection(TextTransforms.ToUpper);
        Assert.Equal("ABC", document.Text);

        document.EditorDocument.UndoStack.Undo();
        Assert.Equal("abc", document.Text);
    }

    /// <summary>メニュー・パレット・キー割り当てが同じカタログを読んでいることの確認。</summary>
    [Fact]
    public void EveryCommandIdHasACatalogEntry()
    {
        var declared = Enum.GetValues<EditorCommandId>();
        var listed = EditorCommandCatalog.All.Select(command => command.Id).ToHashSet();

        Assert.Equal(declared.Length, EditorCommandCatalog.All.Count);
        Assert.All(declared, id => Assert.Contains(id, listed));
    }

    // ===== ブックマーク =====

    [Fact]
    public void BookmarksToggleOnTheCaretLine()
    {
        var document = CreateDocument("one\ntwo\nthree");
        document.CaretIndex = document.GetLineStartOffset(2);

        Assert.True(document.ToggleBookmark());
        Assert.Equal([2], document.Bookmarks.Lines);

        Assert.False(document.ToggleBookmark());
        Assert.Empty(document.Bookmarks.Lines);
    }

    /// <summary>行番号で覚えると上に行を足しただけで印がずれる。アンカーで追随することを確かめる。</summary>
    [Fact]
    public void BookmarksFollowTheLineWhenTextIsInsertedAbove()
    {
        var document = CreateDocument("one\ntwo\nthree");
        document.CaretIndex = document.GetLineStartOffset(3);
        document.ToggleBookmark();
        Assert.Equal([3], document.Bookmarks.Lines);

        document.EditorDocument.Insert(0, "zero\n");

        Assert.Equal([4], document.Bookmarks.Lines);
    }

    /// <summary>印を付けた行そのものが消えたら印も消える。</summary>
    [Fact]
    public void BookmarksDisappearWithTheirLine()
    {
        var document = CreateDocument("one\ntwo\nthree");
        document.CaretIndex = document.GetLineStartOffset(2);
        document.ToggleBookmark();

        document.DeleteLines();

        Assert.Empty(document.Bookmarks.Lines);
    }

    [Fact]
    public void BookmarkNavigationWrapsAround()
    {
        var document = CreateDocument("a\nb\nc\nd");
        foreach (var line in (int[])[2, 4])
        {
            document.CaretIndex = document.GetLineStartOffset(line);
            document.ToggleBookmark();
        }

        document.CaretIndex = 0;
        Assert.True(document.GoToNextBookmark());
        Assert.Equal(2, document.CurrentLine);

        Assert.True(document.GoToNextBookmark());
        Assert.Equal(4, document.CurrentLine);

        // 末尾の次は先頭へ回り込む（sakura と同じ巡回）。
        Assert.True(document.GoToNextBookmark());
        Assert.Equal(2, document.CurrentLine);

        Assert.True(document.GoToPreviousBookmark());
        Assert.Equal(4, document.CurrentLine);
    }

    [Fact]
    public void BookmarkNavigationReportsWhenThereAreNone()
    {
        var document = CreateDocument("a\nb");

        Assert.False(document.GoToNextBookmark());
        Assert.False(document.GoToPreviousBookmark());
    }

    [Fact]
    public void PatternMarkingHitsEveryMatchingLine()
    {
        var document = CreateDocument("alpha\nbeta\nalphabet\ngamma");

        var marked = document.Bookmarks.MarkMatching(line => line.Contains("alpha", StringComparison.Ordinal));

        Assert.Equal(2, marked);
        Assert.Equal([1, 3], document.Bookmarks.Lines);
    }

    // ===== 対括弧の検索 =====

    [Theory]
    [InlineData("(abc)", 0, 4)]   // 開き括弧の上
    [InlineData("(abc)", 4, 0)]   // 閉じ括弧の上
    [InlineData("((a))", 0, 4)]   // 入れ子の外側
    [InlineData("((a))", 1, 3)]   // 入れ子の内側
    [InlineData("「あ」", 0, 2)]   // 和文の括弧
    public void MatchingBracketIsFound(string text, int caret, int expected)
    {
        var document = CreateDocument(text);
        document.CaretIndex = caret;

        Assert.Equal(expected, document.FindMatchingBracket());
    }

    [Fact]
    public void UnbalancedBracketHasNoMatch()
    {
        var document = CreateDocument("(abc");
        document.CaretIndex = 0;

        Assert.Null(document.FindMatchingBracket());
    }

    [Fact]
    public void CaretAwayFromBracketsFindsNothing()
    {
        var document = CreateDocument("abc");
        document.CaretIndex = 2;

        Assert.Null(document.FindMatchingBracket());
    }

    private static DocumentViewModel CreateDocument(string text)
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Text = text;
        return document;
    }
}
