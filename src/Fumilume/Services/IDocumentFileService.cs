using Fumilume.Models;

namespace Fumilume.Services;

public interface IDocumentFileService
{
    Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default);

    Task WriteAsync(
        string path,
        TextDocumentContent content,
        CancellationToken cancellationToken = default);
}
