using Fumilume.Models;
using Fumilume.Services;

namespace Fumilume.Tests;

internal sealed class EmptyDocumentFileService : IDocumentFileService
{
    public Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(new TextDocumentContent(string.Empty, DocumentEncoding.Utf8, Environment.NewLine));

    public Task WriteAsync(
        string path,
        TextDocumentContent content,
        bool createBackup = false,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
