using Fumilume.Models;
using Fumilume.ViewModels;

namespace Fumilume.Tests;

public sealed class DocumentViewModelTests
{
    [Fact]
    public void EditingUpdatesDirtyStateAndStatistics()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);

        document.Text = "alpha\nbeta";
        document.CaretIndex = 8;

        Assert.True(document.IsModified);
        Assert.Equal(2, document.LineCount);
        Assert.Equal(10, document.CharacterCount);
        Assert.Equal("行 2、列 3", document.LineColumnText);
        Assert.Contains("●", document.DisplayTitle);
    }

    [Theory]
    [InlineData("alpha\rbeta", 2)]
    [InlineData("alpha\r\nbeta\ngamma", 3)]
    public void StatisticsCountEverySupportedNewLine(string text, int expectedLineCount)
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask)
        {
            Text = text,
        };

        Assert.Equal(expectedLineCount, document.LineCount);
        Assert.Equal(document.EditorDocument.LineCount, document.LineCount);
    }

    [Fact]
    public void LoadingDocumentResetsDirtyStateAndKeepsMetadata()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        var path = Path.Combine(Path.GetTempPath(), "sample.txt");

        document.Load(path, new TextDocumentContent("hello\nworld", DocumentEncoding.Utf8Bom, "\n"));

        Assert.False(document.IsModified);
        Assert.Equal("sample.txt", document.DisplayName);
        Assert.Equal("UTF-8 BOM", document.EncodingLabel);
        Assert.Equal("LF", document.NewLineLabel);
    }

    [Fact]
    public void ChangingEncodingOrNewLineMarksTheDocumentAsModified()
    {
        var document = new DocumentViewModel("無題", _ => Task.CompletedTask);
        document.Load(
            Path.Combine(Path.GetTempPath(), "metadata.txt"),
            new TextDocumentContent("hello\n", DocumentEncoding.Utf8, DocumentNewLines.Lf));

        document.Encoding = DocumentEncoding.Utf16LittleEndian;

        Assert.True(document.IsModified);
        Assert.Equal("UTF-16 LE", document.EncodingLabel);

        document.Load(
            Path.Combine(Path.GetTempPath(), "metadata.txt"),
            new TextDocumentContent("hello\n", DocumentEncoding.Utf8, DocumentNewLines.Lf));
        document.NewLine = DocumentNewLines.CrLf;

        Assert.True(document.IsModified);
        Assert.Equal("CRLF", document.NewLineLabel);
    }
}
