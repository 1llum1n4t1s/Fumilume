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

        document.ToggleMarkdownPreview();
        Assert.False(document.IsMarkdownPreview);

        document.MarkSaved(@"C:\tmp\readme.md");
        document.ToggleMarkdownPreview();

        Assert.True(document.IsMarkdown);
        Assert.True(document.IsMarkdownPreview);
        Assert.False(document.IsEditorVisible);
    }
}
