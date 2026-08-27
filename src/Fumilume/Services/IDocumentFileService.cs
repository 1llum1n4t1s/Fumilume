using Fumilume.Models;

namespace Fumilume.Services;

public interface IDocumentFileService
{
    Task<TextDocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <param name="createBackup">
    /// 上書きの前に <c>.bak</c> を残すか（設定「保存時にバックアップを作成する」）。
    /// </param>
    Task WriteAsync(
        string path,
        TextDocumentContent content,
        bool createBackup = false,
        CancellationToken cancellationToken = default);
}
