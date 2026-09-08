using System.Text;
using Fumilume.Models;
using Fumilume.Services;

namespace Fumilume.Tests;

public sealed class DocumentFileServiceTests
{
    [Theory]
    [InlineData(DocumentEncoding.Utf8, "")]
    [InlineData(DocumentEncoding.Utf8, "ログ一行目\r\n")]
    [InlineData(DocumentEncoding.Utf8Bom, "ログ一行目\r\n")]
    [InlineData(DocumentEncoding.Utf16LittleEndian, "ログ一行目\r\n")]
    [InlineData(DocumentEncoding.Utf16BigEndian, "ログ一行目\r\n")]
    public async Task ReadWhileWriterIsOpenPreservesContentAndAllowsFurtherWrites(
        DocumentEncoding encoding, string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Fumilume-{Guid.NewGuid():N}.log");
        try
        {
            var service = new DocumentFileService();
            await service.WriteAsync(path, new TextDocumentContent(text, encoding, "\r\n"),
                cancellationToken: TestContext.Current.CancellationToken);
            await using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            writer.Seek(0, SeekOrigin.End);

            var loaded = await service.ReadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(text, loaded.Text);
            Assert.Equal(encoding, loaded.Encoding);
            Assert.Equal(text.Length == 0 ? Environment.NewLine : "\r\n", loaded.NewLine);
            await writer.WriteAsync(new byte[] { 0x0A }, TestContext.Current.CancellationToken);
            await writer.FlushAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadRejectsExclusivelyLockedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Fumilume-{Guid.NewGuid():N}.log");
        try
        {
            await using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await Assert.ThrowsAsync<IOException>(() =>
                new DocumentFileService().ReadAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

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

    [Fact]
    public void TemporaryFileCleanupDoesNotThrowWhenTheFileIsLocked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Fumilume-{Guid.NewGuid():N}.tmp");
        try
        {
            using var locked = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

            DocumentFileService.TryDeleteTemporaryFile(path);

            Assert.True(File.Exists(path));
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
