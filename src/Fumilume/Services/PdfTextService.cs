using Avalonia;
using Fumilume.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;

namespace Fumilume.Services;

/// <summary>描画から独立して、必要なページだけPDFの文字を読み取る。</summary>
public sealed class PdfTextService : IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetimeLock = new();
    private PdfDocument? _document;
    private bool _disposed;
    private bool _reading;

    public PdfTextService(string filePath) => _filePath = Path.GetFullPath(filePath);

    public async Task<PdfTextPage> ReadPageAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadPage(pageIndex, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private PdfTextPage ReadPage(int pageIndex, CancellationToken cancellationToken)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _reading = true;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _document ??= PdfDocument.Open(_filePath);
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _document.NumberOfPages);
            if (!CanCopyText(_document))
            {
                throw new UnauthorizedAccessException("このPDFではテキストのコピーが許可されていません。");
            }

            var page = _document.GetPage(pageIndex + 1);
            cancellationToken.ThrowIfCancellationRequested();
            return Extract(page, cancellationToken);
        }
        finally
        {
            lock (_lifetimeLock)
            {
                _reading = false;
                if (_disposed)
                {
                    _document?.Dispose();
                    _document = null;
                }
            }
        }
    }

    private static bool CanCopyText(PdfDocument document)
    {
        if (!document.IsEncrypted)
        {
            return true;
        }

        var token = document.Structure.Trailer.EncryptionToken;
        if (token is IndirectReferenceToken reference)
        {
            token = document.Structure.GetObject(reference.Data).Data;
        }

        // ISO 32000のPの第5ビットが、文字・画像の抽出を許可する。
        return token is DictionaryToken dictionary
            && dictionary.TryGet(NameToken.Create("P"), out NumericToken permissions)
            && (permissions.Int & 16) != 0;
    }

    private static PdfTextPage Extract(Page page, CancellationToken cancellationToken)
    {
        var size = new Size(page.Width, page.Height);
        var visible = new Rect(size);
        var lines = new List<TextLine>();
        foreach (var letter in page.Letters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(letter.Value))
            {
                continue;
            }

            // PdfPig 0.1.16がCropBoxの移動とRotateを適用する。ここではY軸だけを反転する。
            var box = letter.GlyphRectangleLoose;
            var points = new[] { box.BottomLeft, box.BottomRight, box.TopLeft, box.TopRight };
            var left = points.Min(p => p.X);
            var right = points.Max(p => p.X);
            var top = page.Height - points.Max(p => p.Y);
            var bottom = page.Height - points.Min(p => p.Y);
            var bounds = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)).Intersect(visible);
            if (bounds.Width <= 0 || bounds.Height <= 0
                || !double.IsFinite(bounds.X + bounds.Y + bounds.Width + bounds.Height))
            {
                continue;
            }

            var start = new Point(letter.StartBaseLine.X, page.Height - letter.StartBaseLine.Y);
            var end = new Point(letter.EndBaseLine.X, page.Height - letter.EndBaseLine.Y);
            var delta = new Vector(end.X - start.X, end.Y - start.Y);
            var length = delta.Length;
            var direction = length > 0.001 ? delta / length : new Vector(1, 0);
            var normal = new Vector(-direction.Y, direction.X);
            var across = start.X * normal.X + start.Y * normal.Y;
            var along = start.X * direction.X + start.Y * direction.Y;
            var fontSize = Math.Max(1, letter.PointSize);
            var line = lines.FirstOrDefault(candidate =>
                Dot(candidate.Direction, direction) > 0.98
                && Math.Abs(candidate.Across - across) <= Math.Max(candidate.FontSize, fontSize) * 0.35);
            if (line is null)
            {
                line = new TextLine(direction, across, fontSize, bounds);
                lines.Add(line);
            }

            line.Letters.Add(new TextGlyph(letter.Value, bounds, along, along + length, fontSize));
        }

        var characters = new List<PdfTextCharacter>();
        // 同一方向の行はベースラインの法線方向に並べる。縦書きは右から左になる。
        var groups = new List<List<TextLine>>();
        foreach (var line in lines)
        {
            var group = groups.FirstOrDefault(candidate => Dot(candidate[0].Direction, line.Direction) > 0.98);
            if (group is null)
            {
                group = [];
                groups.Add(group);
            }
            group.Add(line);
        }

        foreach (var group in groups.OrderBy(g => g.Min(l => l.Bounds.Top)).ThenBy(g => g.Min(l => l.Bounds.Left)))
        {
            foreach (var line in group.OrderBy(l => l.Across))
            {
                TextGlyph? previous = null;
                foreach (var glyph in line.Letters.OrderBy(l => l.Along))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var separator = previous is null && characters.Count > 0 ? "\n" : "";
                    if (previous is not null
                        && !char.IsWhiteSpace(previous.Text[^1]) && !char.IsWhiteSpace(glyph.Text[0])
                        && glyph.Along - previous.End > Math.Min(previous.FontSize, glyph.FontSize) * 0.2)
                    {
                        separator = " ";
                    }

                    characters.Add(new PdfTextCharacter(glyph.Text, glyph.Bounds, separator));
                    previous = glyph;
                }
            }
        }

        return new PdfTextPage(size, characters.ToArray());
    }

    private static double Dot(Vector first, Vector second) => first.X * second.X + first.Y * second.Y;

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            _disposed = true;
            // 同期抽出中は呼出元を待たせず、ワーカーのfinallyで閉じる。
            if (!_reading)
            {
                _document?.Dispose();
                _document = null;
            }
        }
    }

    private sealed record TextGlyph(string Text, Rect Bounds, double Along, double End, double FontSize);

    private sealed class TextLine(Vector direction, double across, double fontSize, Rect bounds)
    {
        public Vector Direction { get; } = direction;
        public double Across { get; } = across;
        public double FontSize { get; } = fontSize;
        public Rect Bounds { get; } = bounds;
        public List<TextGlyph> Letters { get; } = [];
    }
}
