using Fumilume.Models;
using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class DocumentFormattingServiceTests
{
    [Fact]
    public void JsonUsesConfiguredSpacesAndPreservesTheNewLine()
    {
        var result = DocumentFormattingService.Format(
            @"C:\tmp\settings.json",
            "{\"name\":\"Fumilume\",\"items\":[1,2]}\r\n",
            DocumentNewLines.CrLf,
            DocumentEncoding.Utf8,
            indentationSize: 2,
            convertTabsToSpaces: true);

        Assert.Equal(DocumentFormatOutcome.Success, result.Outcome);
        Assert.Equal(
            "{\r\n  \"name\": \"Fumilume\",\r\n  \"items\": [\r\n    1,\r\n    2\r\n  ]\r\n}\r\n",
            result.Text);
    }

    [Fact]
    public void XmlKeepsTheDeclaredDocumentEncoding()
    {
        var result = DocumentFormattingService.Format(
            @"C:\tmp\view.axaml",
            "<?xml version=\"1.0\" encoding=\"utf-16\"?><Root><Child Value=\"1\" /></Root>",
            DocumentNewLines.Lf,
            DocumentEncoding.Utf16LittleEndian,
            indentationSize: 4,
            convertTabsToSpaces: false);

        Assert.Equal(DocumentFormatOutcome.Success, result.Outcome);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>\n", result.Text);
        Assert.Contains("\n\t<Child Value=\"1\" />\n", result.Text);
    }

    [Fact]
    public void BraceLanguageIgnoresBracesInsideStringsAndComments()
    {
        const string source = "class Sample\n{\nvoid Write(\nstring value)\n{\n// } はコメント\nConsole.WriteLine(\"{value}\");\n}\n}";

        var result = DocumentFormattingService.Format(
            @"C:\tmp\Sample.cs",
            source,
            DocumentNewLines.Lf,
            DocumentEncoding.Utf8,
            indentationSize: 4,
            convertTabsToSpaces: true);

        Assert.Equal(DocumentFormatOutcome.Success, result.Outcome);
        Assert.Equal(
            "class Sample\n{\n    void Write(\n        string value)\n    {\n        // } はコメント\n        Console.WriteLine(\"{value}\");\n    }\n}",
            result.Text);
    }

    [Fact]
    public void InvalidSyntaxDoesNotReturnReplacementText()
    {
        var result = DocumentFormattingService.Format(
            @"C:\tmp\Sample.cs",
            "class Sample\n{",
            DocumentNewLines.Lf,
            DocumentEncoding.Utf8,
            indentationSize: 4,
            convertTabsToSpaces: true);

        Assert.Equal(DocumentFormatOutcome.Invalid, result.Outcome);
        Assert.Null(result.Text);
    }

    [Fact]
    public void IndentationSensitiveFormatsAreNotChanged()
    {
        var result = DocumentFormattingService.Format(
            @"C:\tmp\settings.yml",
            "root:\n  child: value",
            DocumentNewLines.Lf,
            DocumentEncoding.Utf8,
            indentationSize: 4,
            convertTabsToSpaces: true);

        Assert.Equal(DocumentFormatOutcome.Unsupported, result.Outcome);
        Assert.Null(result.Text);
    }
}
