using PdfOcrRenamer;

namespace PdfOcrRenamer.Tests;

public class PdfOpsTests
{
    private static string MakePdf()
    {
        var pdf = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\nxref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000062 00000 n \n0000000115 00000 n \ntrailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n164\n%%EOF\n";
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        File.WriteAllText(path, pdf);
        return path;
    }

    [Fact]
    public void BuildTargetName_ContainsDateTypeAndStem()
    {
        var src = MakePdf();
        try
        {
            var name = PdfOps.BuildTargetName(src, "invoice", "ACME Ltd");
            Assert.EndsWith(".pdf", name);
            Assert.Contains("invoice", name);
            Assert.EndsWith("_.pdf"[..0] + Path.GetFileNameWithoutExtension(src) + ".pdf", name);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void BuildTargetName_SenderIncluded()
    {
        var src = MakePdf();
        try
        {
            var withSender = PdfOps.BuildTargetName(src, "invoice", "acme");
            var withoutSender = PdfOps.BuildTargetName(src, "invoice", "");
            Assert.Contains("acme", withSender);
            Assert.DoesNotContain("acme", withoutSender);
        }
        finally { File.Delete(src); }
    }

    [Fact]
    public void EmbedAndWrite_CreatesProcessedAndMdFiles()
    {
        var src = MakePdf();
        var watch = Path.Combine(Path.GetTempPath(), "watch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(watch);
        try
        {
            var target = PdfOps.EmbedAndWrite(src, "# hello", "letter", watch, keepMdSidecar: true, sender: "acme");
            Assert.True(File.Exists(target));
            Assert.True(Directory.Exists(Path.Combine(watch, "processed")));
            var md = Path.Combine(watch, "md");
            Assert.True(Directory.Exists(md));
            Assert.Equal(1, Directory.GetFiles(md, "*.md").Length);
            var content = File.ReadAllText(target);
            Assert.Contains("/EmbeddedFiles", content);
            Assert.Contains("ocr.md", content);
            Assert.Contains("# hello", content);
        }
        finally
        {
            File.Delete(src);
            Directory.Delete(watch, true);
        }
    }

    [Fact]
    public void EmbedAndWrite_NeverOverwritesExistingTarget()
    {
        var src = MakePdf();
        var watch = Path.Combine(Path.GetTempPath(), "watch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(watch);
        try
        {
            var first = PdfOps.EmbedAndWrite(src, "# one", "letter", watch, keepMdSidecar: false);
            var second = PdfOps.EmbedAndWrite(src, "# two", "letter", watch, keepMdSidecar: false);
            Assert.NotEqual(first, second);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.EndsWith("_1.pdf", second);
    }
        finally
        {
            File.Delete(src);
            Directory.Delete(watch, true);
        }
    }
}
