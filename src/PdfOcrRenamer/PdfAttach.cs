using System.Text;
using System.Text.RegularExpressions;

namespace PdfOcrRenamer;

public static partial class PdfAttach
{
    public static void EmbedAttachment(string pdfPath, string attachmentName, byte[] data, string outputPath)
    {
        var original = File.ReadAllBytes(pdfPath);
        var startxref = FindStartXref(original);
        var rootNum = ParseTrailerRootNum(original, startxref);
        var catalogText = ReadIndirectObjectBody(original, rootNum)
            ?? throw new InvalidOperationException($"Catalog object {rootNum} not found in PDF.");

        var maxObj = MaxExistingObjectNumber(original);
        var namesNum = maxObj + 1;
        var filespecNum = maxObj + 2;
        var streamNum = maxObj + 3;

        var newCatalog = InsertBeforeClosingBracket(
            catalogText,
            $"/Names {namesNum} 0 R");

        using var output = new MemoryStream();
        output.Write(original, 0, original.Length);
        Write(output, "\n");

        var offsets = new Dictionary<int, long>();

        offsets[namesNum] = output.Position;
        Write(output, $"{namesNum} 0 obj\n<< /EmbeddedFiles << /Names [ ({Escape(attachmentName)}) {filespecNum} 0 R ] >> >>\nendobj\n");

        offsets[filespecNum] = output.Position;
        Write(output, $"{filespecNum} 0 obj\n<< /Type /Filespec /F ({Escape(attachmentName)}) /UF ({Escape(attachmentName)}) /Desc (OCR result) /EF << /F {streamNum} 0 R >> >>\nendobj\n");

        offsets[streamNum] = output.Position;
        Write(output, $"{streamNum} 0 obj\n<< /Type /EmbeddedFile /Subtype /text#2Fplain /Size {data.Length} /Params << /Size {data.Length} >> >>\nstream\n");
        output.Write(data, 0, data.Length);
        Write(output, "\nendstream\nendobj\n");

        offsets[rootNum] = output.Position;
        Write(output, $"{rootNum} 0 obj\n");
        Write(output, newCatalog);
        Write(output, "\nendobj\n");

        var xrefPos = output.Position;
        var xref = new StringBuilder();
        xref.Append("xref\n");
        xref.Append($"{rootNum} 1\n");
        xref.Append($"{offsets[rootNum]:D10} 00000 n \n");
        xref.Append($"{namesNum} 3\n");
        xref.Append($"{offsets[namesNum]:D10} 00000 n \n");
        xref.Append($"{offsets[filespecNum]:D10} 00000 n \n");
        xref.Append($"{offsets[streamNum]:D10} 00000 n \n");
        xref.Append($"trailer\n<< /Size {maxObj + 4} /Root {rootNum} 0 R /Prev {startxref} >>\nstartxref\n{xrefPos}\n%%EOF\n");
        Write(output, xref.ToString());

        File.WriteAllBytes(outputPath, output.ToArray());
    }

    private static void Write(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));

    private static string Escape(string name) =>
        name.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static int FindStartXref(byte[] pdf)
    {
        var tailLen = Math.Min(2048, pdf.Length);
        var tail = Encoding.ASCII.GetString(pdf, pdf.Length - tailLen, tailLen);
        var idx = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("Not a valid PDF: startxref not found.");
        var rest = tail[(idx + 9)..].TrimStart('\n', '\r', ' ', '\t');
        var end = rest.IndexOfAny(new[] { '\n', '\r', ' ' });
        if (!int.TryParse(end < 0 ? rest : rest[..end], out var startxref))
            throw new InvalidOperationException("Not a valid PDF: cannot parse startxref offset.");
        return startxref;
    }

    private static int ParseTrailerRootNum(byte[] pdf, int startxref)
    {
        var from = Math.Max(0, startxref - 4096);
        var len = Math.Min(8192, pdf.Length - from);
        var text = Encoding.Latin1.GetString(pdf, from, len);
        var m = Regex.Match(text, @"/Root\s+(\d+)\s+\d+\s+R");
        if (m.Success) return int.Parse(m.Groups[1].Value);

        var xrefText = Encoding.Latin1.GetString(pdf, startxref, Math.Min(4096, pdf.Length - startxref));
        var isStream = xrefText.TrimStart().StartsWith("/Type /XRef") || Regex.IsMatch(xrefText, @"/Type\s*/XRef");
        if (isStream)
        {
            var rm = Regex.Match(xrefText, @"/Root\s+(\d+)\s+\d+\s+R");
            if (rm.Success) return int.Parse(rm.Groups[1].Value);
        }

        var all = Encoding.Latin1.GetString(pdf);
        var catalog = Regex.Match(all, @"(\d+)\s+\d+\s+obj\s*<<[^>]*?/Type\s*/Catalog");
        if (!catalog.Success)
            catalog = Regex.Match(all, @"(\d+)\s+\d+\s+obj\s*<<\s*/Type\s*/Catalog");
        if (!catalog.Success)
            throw new InvalidOperationException("Not a valid PDF: /Root not found in trailer.");
        return int.Parse(catalog.Groups[1].Value);
    }

    private static int MaxExistingObjectNumber(byte[] pdf)
    {
        var text = Encoding.ASCII.GetString(pdf);
        var max = 0;
        foreach (Match m in ObjectHeaderRegex().Matches(text))
            max = Math.Max(max, int.Parse(m.Groups[1].Value));
        return max;
    }

    private static string? ReadIndirectObjectBody(byte[] pdf, int num)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var m = Regex.Match(text, $"{num}\\s+\\d+\\s+obj(.*?)endobj", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value.Trim('\n', '\r') : null;
    }

    private static string InsertBeforeClosingBracket(string catalogText, string entry)
    {
        var idx = catalogText.LastIndexOf(">>");
        if (idx < 0) throw new InvalidOperationException("Malformed catalog dictionary.");
        return catalogText[..idx] + entry + " " + catalogText[idx..];
    }

    [GeneratedRegex(@"(\d+)\s+\d+\s+obj")]
    private static partial Regex ObjectHeaderRegex();
}
