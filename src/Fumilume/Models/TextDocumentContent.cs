namespace Fumilume.Models;

public enum DocumentEncoding
{
    Utf8,
    Utf8Bom,
    Utf16LittleEndian,
    Utf16BigEndian,
}

public sealed record TextDocumentContent(
    string Text,
    DocumentEncoding Encoding,
    string NewLine);
