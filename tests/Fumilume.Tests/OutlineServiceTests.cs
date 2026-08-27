using Fumilume.Services;

namespace Fumilume.Tests;

/// <summary>
/// アウトライン解析。拾いすぎると「ここに無いなら本文にも無い」と誤読させるので、
/// 拾えることより「宣言でない行を拾わないこと」を厚く確かめる。
/// </summary>
public sealed class OutlineServiceTests
{
    [Fact]
    public void MarkdownHeadingsKeepTheirLevelAndLine()
    {
        var items = OutlineService.Parse("notes.md", "# 表題\n本文\n\n## 節\n### 小節\n");

        Assert.Equal(
            [(1, "表題", 1), (2, "節", 4), (3, "小節", 5)],
            items.Select(item => (item.Level, item.Title, item.LineNumber)));
    }

    [Fact]
    public void ClosingHashesAreNotPartOfTheTitle()
    {
        var items = OutlineService.Parse("notes.md", "## 節 ##\n");

        Assert.Equal("節", Assert.Single(items).Title);
    }

    [Fact]
    public void HeadingsInsideFencedCodeAreIgnored()
    {
        var items = OutlineService.Parse("notes.md", "# 本物\n\n```\n# 見せかけ\n```\n\n# もう 1 つ\n");

        Assert.Equal(["本物", "もう 1 つ"], items.Select(item => item.Title));
    }

    [Fact]
    public void ADifferentFenceMarkerDoesNotCloseTheBlock()
    {
        var items = OutlineService.Parse("notes.md", "```\n~~~\n# 見せかけ\n~~~\n```\n# 本物\n");

        Assert.Equal("本物", Assert.Single(items).Title);
    }

    [Fact]
    public void SetextHeadingsAreRecognised()
    {
        var items = OutlineService.Parse("notes.md", "表題\n====\n\n節\n----\n");

        Assert.Equal([(1, "表題", 1), (2, "節", 4)], items.Select(item => (item.Level, item.Title, item.LineNumber)));
    }

    [Fact]
    public void FrontMatterIsNotReadAsAHeading()
    {
        var items = OutlineService.Parse("notes.md", "---\ntitle: 見せかけ\n---\n\n# 本物\n");

        Assert.Equal("本物", Assert.Single(items).Title);
    }

    [Fact]
    public void TypesAndMembersNestByBraceDepth()
    {
        const string source = """
            namespace Fumilume.Sample;

            public sealed class Widget
            {
                public string Name { get; set; }

                public void Render(int scale)
                {
                }
            }
            """;

        var items = OutlineService.Parse("Widget.cs", source);

        Assert.Equal(
            [(1, "Fumilume.Sample", 1), (1, "Widget", 3), (2, "Name", 5), (2, "Render", 7)],
            items.Select(item => (item.Level, item.Title, item.LineNumber)));
    }

    [Fact]
    public void BlockScopedNamespacesPushTheirContentsOneLevelDeeper()
    {
        const string source = """
            namespace Sample
            {
                public class Widget
                {
                }
            }
            """;

        var items = OutlineService.Parse("Widget.cs", source);

        Assert.Equal([(1, "Sample", 1), (2, "Widget", 3)], items.Select(item => (item.Level, item.Title, item.LineNumber)));
    }

    [Theory]
    [InlineData("if (ready)")]
    [InlineData("} else if (ready) {")]
    [InlineData("foreach (var item in items)")]
    [InlineData("using (var stream = Open())")]
    [InlineData("Console.WriteLine(value);")]
    [InlineData("throw new InvalidOperationException(\"だめ\");")]
    [InlineData("await RunAsync(token);")]
    [InlineData("return Compute(value);")]
    [InlineData("[Theory, InlineData(1)]")]
    [InlineData("var widget = new Widget(scale);")]
    [InlineData(".Where(item => item.Ready)")]
    public void ControlFlowAndCallsAreNotDeclarations(string line)
    {
        var source = "public class Widget\n{\n    " + line + "\n}\n";

        Assert.Equal(["Widget"], OutlineService.Parse("Widget.cs", source).Select(item => item.Title));
    }

    [Fact]
    public void BracesInsideStringsAndCommentsDoNotShiftTheDepth()
    {
        const string source = """
            public class Widget
            {
                public string Pattern => "{{{";

                // } これも数えない
                public void Render()
                {
                }
            }
            """;

        var items = OutlineService.Parse("Widget.cs", source);

        Assert.Equal(
            [(1, "Widget", 1), (2, "Pattern", 3), (2, "Render", 6)],
            items.Select(item => (item.Level, item.Title, item.LineNumber)));
    }

    [Fact]
    public void UnsupportedExtensionsReturnNothing()
    {
        Assert.False(OutlineService.IsSupported("readme.txt"));
        Assert.Empty(OutlineService.Parse("readme.txt", "# これは見出しではない\n"));
    }

    [Fact]
    public void UntitledDocumentsAreUnsupported()
    {
        Assert.False(OutlineService.IsSupported(null));
        Assert.Empty(OutlineService.Parse(null, "# 見出し\n"));
    }

    [Fact]
    public void EmptyTextReturnsNothing()
    {
        Assert.Empty(OutlineService.Parse("notes.md", string.Empty));
        Assert.Empty(OutlineService.Parse("notes.md", null));
    }

    [Fact]
    public void TooManyHeadingsAreCutOffAtTheLimit()
    {
        var source = string.Join('\n', Enumerable.Range(0, OutlineService.MaximumItems + 50).Select(i => $"# 見出し {i}"));

        Assert.Equal(OutlineService.MaximumItems, OutlineService.Parse("notes.md", source).Count);
    }
}
