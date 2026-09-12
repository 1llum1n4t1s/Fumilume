using System.Text;
using Fumilume.Models;

namespace Fumilume.Services;

/// <summary>文書の文字コードを厳密に判定し、保存用のエンコーダーを提供する。</summary>
internal static class DocumentEncodingService
{
    private static readonly UTF8Encoding Utf8Strict = new(false, true);
    private static readonly UTF8Encoding Utf8BomStrict = new(true, true);
    private static readonly UnicodeEncoding Utf16LittleEndianStrict = new(false, true, true);
    private static readonly UnicodeEncoding Utf16BigEndianStrict = new(true, true, true);
    private static readonly UnicodeEncoding Utf16LittleEndianNoBomStrict = new(false, false, true);
    private static readonly UnicodeEncoding Utf16BigEndianNoBomStrict = new(true, false, true);
    private static readonly UTF32Encoding Utf32LittleEndianStrict = new(false, true, true);
    private static readonly UTF32Encoding Utf32BigEndianStrict = new(true, true, true);
    private static readonly UTF32Encoding Utf32LittleEndianNoBomStrict = new(false, false, true);
    private static readonly UTF32Encoding Utf32BigEndianNoBomStrict = new(true, false, true);
    private static readonly Encoding ShiftJisStrict = CreateCodePageEncoding(932);
    private static readonly Encoding EucJpStrict = CreateCodePageEncoding(51932);
    private static readonly Encoding Iso2022JpStrict = CreateCodePageEncoding(50220);

    public static TextDocumentContent Decode(byte[] bytes)
    {
        if (DetectUnicodeEncoding(bytes) is { } detected)
        {
            return DecodeWith(bytes, detected.Kind, detected.PreambleLength, detected.Decoder);
        }

        // ISO-2022-JP はエスケープシーケンスが識別子になるため、UTF-8より先に判定する。
        if (ContainsIso2022JpEscape(bytes) && TryDecode(bytes, Iso2022JpStrict, out var text))
        {
            return CreateContent(text, DocumentEncoding.Iso2022Jp);
        }

        if (TryDecode(bytes, Utf8Strict, out text))
        {
            return CreateContent(text, DocumentEncoding.Utf8);
        }

        // EUC-JP のバイト構造は Shift_JIS より狭い。先に厳密判定すると、一般的な日本語本文を
        // Shift_JIS の半角文字列として誤って受け入れるのを避けられる。
        if (ContainsNonAscii(bytes) && TryDecode(bytes, EucJpStrict, out text))
        {
            return CreateContent(text, DocumentEncoding.EucJp);
        }

        if (ContainsNonAscii(bytes) && TryDecode(bytes, ShiftJisStrict, out text))
        {
            return CreateContent(text, DocumentEncoding.ShiftJis);
        }

        throw CreateUnsupportedEncodingException();
    }

    public static Encoding GetEncoding(DocumentEncoding encoding)
        => encoding switch
        {
            DocumentEncoding.Utf8 => Utf8Strict,
            DocumentEncoding.Utf8Bom => Utf8BomStrict,
            DocumentEncoding.Utf16LittleEndian => Utf16LittleEndianStrict,
            DocumentEncoding.Utf16BigEndian => Utf16BigEndianStrict,
            DocumentEncoding.Utf16LittleEndianNoBom => Utf16LittleEndianNoBomStrict,
            DocumentEncoding.Utf16BigEndianNoBom => Utf16BigEndianNoBomStrict,
            DocumentEncoding.Utf32LittleEndian => Utf32LittleEndianStrict,
            DocumentEncoding.Utf32BigEndian => Utf32BigEndianStrict,
            DocumentEncoding.Utf32LittleEndianNoBom => Utf32LittleEndianNoBomStrict,
            DocumentEncoding.Utf32BigEndianNoBom => Utf32BigEndianNoBomStrict,
            DocumentEncoding.ShiftJis => ShiftJisStrict,
            DocumentEncoding.EucJp => EucJpStrict,
            DocumentEncoding.Iso2022Jp => Iso2022JpStrict,
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };

    public static bool HasRecognizableUnicodeLayout(ReadOnlySpan<byte> bytes)
        => DetectUnicodeEncoding(bytes) is not null;

    public static bool HasIncompleteUtf8Tail(ReadOnlySpan<byte> bytes)
    {
        var firstCandidate = Math.Max(0, bytes.Length - 4);
        for (var start = firstCandidate; start < bytes.Length; start++)
        {
            var expectedContinuationBytes = bytes[start] switch
            {
                >= 0xC2 and <= 0xDF => 1,
                >= 0xE0 and <= 0xEF => 2,
                >= 0xF0 and <= 0xF4 => 3,
                _ => 0,
            };
            var availableContinuationBytes = bytes.Length - start - 1;
            if (expectedContinuationBytes == 0
                || availableContinuationBytes >= expectedContinuationBytes)
            {
                continue;
            }

            var allContinuationBytes = true;
            for (var index = start + 1; index < bytes.Length; index++)
            {
                if ((bytes[index] & 0xC0) == 0x80)
                {
                    continue;
                }

                allContinuationBytes = false;
                break;
            }

            if (allContinuationBytes)
            {
                return true;
            }
        }

        return false;
    }

    private static TextDocumentContent DecodeWith(
        byte[] bytes,
        DocumentEncoding encoding,
        int preambleLength,
        Encoding decoder)
    {
        try
        {
            var text = decoder.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            return CreateContent(text, encoding);
        }
        catch (DecoderFallbackException ex)
        {
            throw CreateUnsupportedEncodingException(ex);
        }
    }

    private static TextDocumentContent CreateContent(string text, DocumentEncoding encoding)
        => new(text, encoding, DetectNewLine(text));

    private static bool TryDecode(byte[] bytes, Encoding decoder, out string text)
    {
        try
        {
            text = decoder.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static (DocumentEncoding Kind, int PreambleLength, Encoding Decoder)? DetectUnicodeEncoding(
        ReadOnlySpan<byte> bytes)
    {
        // UTF-32 LE のBOMはUTF-16 LEのBOMで始まるため、必ずUTF-32を先に調べる。
        if (bytes.Length >= 4
            && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return (DocumentEncoding.Utf32LittleEndian, 4, Utf32LittleEndianStrict);
        }

        if (bytes.Length >= 4
            && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return (DocumentEncoding.Utf32BigEndian, 4, Utf32BigEndianStrict);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (DocumentEncoding.Utf8Bom, 3, Utf8Strict);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (DocumentEncoding.Utf16LittleEndian, 2, Utf16LittleEndianNoBomStrict);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (DocumentEncoding.Utf16BigEndian, 2, Utf16BigEndianNoBomStrict);
        }

        if (LooksLikeBomlessUtf32(bytes, bigEndian: false))
        {
            return (DocumentEncoding.Utf32LittleEndianNoBom, 0, Utf32LittleEndianNoBomStrict);
        }

        if (LooksLikeBomlessUtf32(bytes, bigEndian: true))
        {
            return (DocumentEncoding.Utf32BigEndianNoBom, 0, Utf32BigEndianNoBomStrict);
        }

        if (LooksLikeBomlessUtf16(bytes, bigEndian: false))
        {
            return (DocumentEncoding.Utf16LittleEndianNoBom, 0, Utf16LittleEndianNoBomStrict);
        }

        if (LooksLikeBomlessUtf16(bytes, bigEndian: true))
        {
            return (DocumentEncoding.Utf16BigEndianNoBom, 0, Utf16BigEndianNoBomStrict);
        }

        return null;
    }

    private static Encoding CreateCodePageEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static InvalidDataException CreateUnsupportedEncodingException(Exception? innerException = null)
        => new("対応している文字コードとして判定できませんでした。", innerException);

    private static bool ContainsNonAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value >= 0x80)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsIso2022JpEscape(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index + 2 < bytes.Length; index++)
        {
            if (bytes[index] != 0x1B)
            {
                continue;
            }

            var second = bytes[index + 1];
            var third = bytes[index + 2];
            if ((second == (byte)'(' && third is (byte)'B' or (byte)'I' or (byte)'J')
                || (second == (byte)'$' && third is (byte)'@' or (byte)'B'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeBomlessUtf16(ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        var units = Math.Min(bytes.Length / 2, 2048);
        if (units < 2 || bytes.Length % 2 != 0)
        {
            return false;
        }

        var expectedZeros = 0;
        var unexpectedZeros = 0;
        for (var unit = 0; unit < units; unit++)
        {
            var offset = unit * 2;
            expectedZeros += bytes[offset + (bigEndian ? 0 : 1)] == 0 ? 1 : 0;
            unexpectedZeros += bytes[offset + (bigEndian ? 1 : 0)] == 0 ? 1 : 0;
        }

        return expectedZeros * 5 >= units * 3 && unexpectedZeros * 5 <= units;
    }

    private static bool LooksLikeBomlessUtf32(ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        var units = Math.Min(bytes.Length / 4, 1024);
        if (units < 2 || bytes.Length % 4 != 0)
        {
            return false;
        }

        var expectedZeros = 0;
        var unexpectedZeros = 0;
        for (var unit = 0; unit < units; unit++)
        {
            var offset = unit * 4;
            if (bigEndian)
            {
                expectedZeros += bytes[offset] == 0 ? 1 : 0;
                expectedZeros += bytes[offset + 1] == 0 ? 1 : 0;
                unexpectedZeros += bytes[offset + 3] == 0 ? 1 : 0;
            }
            else
            {
                expectedZeros += bytes[offset + 2] == 0 ? 1 : 0;
                expectedZeros += bytes[offset + 3] == 0 ? 1 : 0;
                unexpectedZeros += bytes[offset] == 0 ? 1 : 0;
            }
        }

        return expectedZeros * 4 >= units * 7 && unexpectedZeros * 5 <= units;
    }

    private static string DetectNewLine(string text)
    {
        var crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
        var lf = text.IndexOf('\n');
        var cr = text.IndexOf('\r');

        if (crlf >= 0 && crlf <= (lf < 0 ? int.MaxValue : lf) && crlf <= (cr < 0 ? int.MaxValue : cr))
        {
            return "\r\n";
        }

        if (lf >= 0 && (cr < 0 || lf < cr))
        {
            return "\n";
        }

        return cr >= 0 ? "\r" : Environment.NewLine;
    }
}
