using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class MarkdownDocumentParserTests
{
    [Fact]
    public void CommonMarkdownBlocksArePreparedForPreview()
    {
        var blocks = MarkdownDocumentParser.Parse(
            """
            # 見出し

            本文 **太字** と [リンク](https://example.com)

            - 項目
            7. 七番目
            > 引用
            ```cs
            var value = 1;
            ```
            ---
            """);

        Assert.Collection(
            blocks,
            block => Assert.Equal(new MarkdownBlock(MarkdownBlockKind.Heading, "見出し", 1), block),
            block => Assert.Equal(new MarkdownBlock(MarkdownBlockKind.Paragraph, "本文 太字 と リンク"), block),
            block => Assert.Equal(new MarkdownBlock(MarkdownBlockKind.Bullet, "項目"), block),
            block => Assert.Equal(new MarkdownBlock(MarkdownBlockKind.Numbered, "七番目", Number: 7), block),
            block => Assert.Equal(new MarkdownBlock(MarkdownBlockKind.Quote, "引用"), block),
            block => Assert.Equal(new MarkdownBlock(MarkdownBlockKind.Code, "var value = 1;"), block),
            block => Assert.Equal(MarkdownBlockKind.Rule, block.Kind));
    }

    [Fact]
    public void PipeTableIsParsedWithAlignmentsAndEscapedPipes()
    {
        var blocks = MarkdownDocumentParser.Parse(
            """
            前置き

            | 名前 | 数 | 備考 |
            |:--|--:|:-:|
            | **りんご** | 3 | a\|b |
            | みかん | 5 |

            後置き
            """);

        Assert.Equal(3, blocks.Count);
        var table = blocks[1].Table;
        Assert.Equal(MarkdownBlockKind.Table, blocks[1].Kind);
        Assert.NotNull(table);
        Assert.Equal(
            [MarkdownColumnAlignment.Left, MarkdownColumnAlignment.Right, MarkdownColumnAlignment.Center],
            table.Alignments);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(["名前", "数", "備考"], table.Rows[0]);
        Assert.Equal(["りんご", "3", "a|b"], table.Rows[1]);
        Assert.Equal(["みかん", "5", ""], table.Rows[2]);
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[2].Kind);
    }

    [Fact]
    public void PipeLineWithoutDelimiterRowStaysParagraph()
    {
        var blocks = MarkdownDocumentParser.Parse("a | b\nc | d");

        var block = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
    }

    [Fact]
    public void CrOnlyMarkdownIsParsedAsSeparateBlocks()
    {
        var blocks = MarkdownDocumentParser.Parse("# 見出し\r\r本文");

        Assert.Collection(
            blocks,
            block => Assert.Equal(new MarkdownBlock(MarkdownBlockKind.Heading, "見出し", 1), block),
            block => Assert.Equal(new MarkdownBlock(MarkdownBlockKind.Paragraph, "本文"), block));
    }

    [Fact]
    public void MarkdownDocumentCanTogglePreviewOnlyAfterItHasMdExtension()
    {
        var document = new Fumilume.ViewModels.DocumentViewModel("無題", _ => Task.CompletedTask);

        document.TogglePreview();
        Assert.False(document.IsMarkdownPreview);

        document.MarkSaved(@"C:\tmp\readme.md");
        document.TogglePreview();

        Assert.True(document.IsMarkdown);
        Assert.True(document.IsMarkdownPreview);
        Assert.False(document.IsEditorVisible);
    }
}
