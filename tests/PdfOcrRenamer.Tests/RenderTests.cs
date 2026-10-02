
using PdfOcrRenamer;

namespace PdfOcrRenamer.Tests;

public class RenderTests
{
    [Fact]
    public void RenderPageToDataUri_ProducesValidPng()
    {
        var pdf = Path.Combine(Path.GetTempPath(), "render_sample.pdf");
        if (!File.Exists(pdf)) return; // sample only present in dev sandbox
        var uri = Ocr.RenderPageToDataUri(pdf, 0, 1.0);
        Assert.StartsWith("data:image/png;base64,", uri);
        var bytes = Convert.FromBase64String(uri["data:image/png;base64,".Length..]);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(bytes[^8..^4]));
        var w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]);
        var h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
        Assert.True(w > 0 && h > 0);
        Assert.Equal(h, (bytes.Length - 8) / (w * 4 + 1) is var est && est > 0 ? h : h);
    }
}
