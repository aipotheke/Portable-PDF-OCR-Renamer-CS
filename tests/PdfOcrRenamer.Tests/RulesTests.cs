using System;
using System.IO;
using PdfOcrRenamer;

namespace PdfOcrRenamer.Tests
{
    public class RulesTests
    {
        [Fact]
        public void ExtractDate_ReturnsCreationDate()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, "x");
            var date = Rules.ExtractDate(path);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", date);
            File.Delete(path);
        }

        [Fact]
        public void SanitizeName_RemovesIllegalChars()
        {
            Assert.Equal("a_b_c", Rules.SanitizeName("a<b>c"));
            Assert.Equal("file", Rules.SanitizeName("???"));
            Assert.Equal("a_b", Rules.SanitizeName("a  b"));
            Assert.DoesNotContain(":", Rules.SanitizeName("a:b"));
        }

        [Fact]
        public void Registry_RoundTrips()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, "test");
            var key = Rules.RegistryKey(path);
            Assert.Contains(path, key);
            Assert.Contains("|", key);
            File.Delete(path);
        }

        [Fact]
        public void IsProcessed_FalseForUnknownFile()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
            File.WriteAllText(path, "pdf");
            try
            {
                string name;
                var processed = Rules.IsProcessed(path, out name);
                Assert.False(processed);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void MarkProcessed_ThenIsProcessedTrue()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
            File.WriteAllText(path, "pdf");
            try
            {
                Rules.MarkProcessed(path, "out.pdf");
                string name;
                var processed = Rules.IsProcessed(path, out name);
                Assert.True(processed);
                Assert.Equal("out.pdf", name);
            }
            finally { File.Delete(path); }
        }
    }
}
