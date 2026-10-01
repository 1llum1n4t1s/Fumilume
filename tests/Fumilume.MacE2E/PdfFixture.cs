using System.Globalization;
using System.Text;

namespace Fumilume.MacE2E;

internal static class PdfFixture
{
    // 非対称な図形と回転ページを含む、外部 PDF ツール不要の決定的な fixture。
    public static void Write(string path, int additionalRectangles = 0)
    {
        var contentBuilder = new StringBuilder("1 0 0 rg 10 20 80 140 re f\n0 0 1 rg 120 220 100 60 re f\n");
        // 並行描画ケースでは外部ツールを使わず、再生成できる描画負荷を加える。
        for (var index = 0; index < additionalRectangles; index++)
            contentBuilder.Append(CultureInfo.InvariantCulture, $"{index % 240} {index % 320} 2 2 re f\n");
        var content = contentBuilder.ToString();
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 320] /Resources << >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 320] /Rotate 90 /Resources << >> /Contents 5 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream",
        ];
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(builder.ToString()));
    }
}
