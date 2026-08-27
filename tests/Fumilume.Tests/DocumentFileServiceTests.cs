using System.Text;
using Fumilume.Models;
using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class DocumentFileServiceTests
{
    [Theory]
    [InlineData(DocumentEncoding.Utf8, false)]
    [InlineData(DocumentEncoding.Utf8Bom, true)]
    [InlineData(DocumentEncoding.Utf16LittleEndian, true)]
    [InlineData(DocumentEncoding.Utf16BigEndian, true)]
    public async Task WriteThenReadPreservesEncodingAndNewLines(DocumentEncoding encoding, bool hasPreamble)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Fumilume-{Guid.NewGuid():N}.txt");
        try
        {
            var service = new DocumentFileService();
            var content = new TextDocumentContent("一行目\n二行目\r\n三行目", encoding, "\r\n");

            await service.WriteAsync(path, content, cancellationToken: TestContext.Current.CancellationToken);
            var loaded = await service.ReadAsync(path, TestContext.Current.CancellationToken);
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(encoding, loaded.Encoding);
            Assert.Equal("\r\n", loaded.NewLine);
            Assert.Equal("一行目\r\n二行目\r\n三行目", loaded.Text);
            Assert.Equal(hasPreamble, HasPreamble(bytes, encoding));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadRejectsInvalidUtf8()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Fumilume-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllBytesAsync(path, [0xC3, 0x28], TestContext.Current.CancellationToken);
            var service = new DocumentFileService();

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.ReadAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool HasPreamble(byte[] bytes, DocumentEncoding encoding)
    {
        var preamble = encoding switch
        {
            DocumentEncoding.Utf8Bom => Encoding.UTF8.GetPreamble(),
            DocumentEncoding.Utf16LittleEndian => Encoding.Unicode.GetPreamble(),
            DocumentEncoding.Utf16BigEndian => Encoding.BigEndianUnicode.GetPreamble(),
            _ => [],
        };
        return preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble);
    }
}
