using System;
using System.IO;
using PdfOcrRenamer;

namespace PdfOcrRenamer.Tests
{
    public class RenderTests
    {
        [Fact]
        public void RenderPageToDataUri_ProducesValidPng()
        {
            var pdf = Path.Combine(Path.GetTempPath(), "render_sample.pdf");
            if (!File.Exists(pdf)) return;
            var uri = Ocr.RenderPageToDataUri(pdf, 0, 1.0);
            Assert.StartsWith("data:image/png;base64,", uri);
            var prefix = "data:image/png;base64,";
            var bytes = Convert.FromBase64String(uri.Substring(prefix.Length));
            var expectedHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            for (var i = 0; i < 8; i++) Assert.Equal(expectedHeader[i], bytes[i]);
            Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 8, 4));
            var w = ReadInt32BigEndian(bytes, 16);
            var h = ReadInt32BigEndian(bytes, 20);
            Assert.True(w > 0 && h > 0);
        }

        private static int ReadInt32BigEndian(byte[] b, int off)
        {
            return (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];
        }
    }
}
