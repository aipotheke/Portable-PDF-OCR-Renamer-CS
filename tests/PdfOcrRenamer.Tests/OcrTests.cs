using PdfOcrRenamer;

namespace PdfOcrRenamer.Tests;

public class OcrTests
{
    [Fact]
    public void ParseAnswer_ParsesTypeAndSender()
    {
        var (matched, sender) = Ocr.ParseAnswer("type: invoice\nsender: ACME GmbH", new List<string> { "invoice", "letter" });
        Assert.Equal("invoice", matched);
        Assert.Equal("ACME GmbH", sender);
    }

    [Fact]
    public void ParseAnswer_UnknownSenderBecomesEmpty()
    {
        var (_, sender) = Ocr.ParseAnswer("type: invoice\nsender: unknown", new List<string> { "invoice" });
        Assert.Equal("", sender);
    }

    [Fact]
    public void ParseAnswer_ExtraLinesIgnored()
    {
        var (matched, _) = Ocr.ParseAnswer("Here is my answer:\ntype: receipt\nsender: Foo Inc\nthanks!", new List<string> { "receipt" });
        Assert.Equal("receipt", matched);
    }

    [Fact]
    public void MatchType_IsCaseInsensitive()
    {
        Assert.Equal("Invoice", Ocr.MatchType(" invoice.", new List<string> { "Invoice" }));
        Assert.Equal("invoice", Ocr.MatchType("INVOICE", new List<string> { "invoice" }));
    }

    [Fact]
    public void MatchType_ReturnsNullForUnknown()
    {
        Assert.Null(Ocr.MatchType("banana", new List<string> { "invoice" }));
    }

    [Fact]
    public void MatchType_StripsPunctuation()
    {
        Assert.Equal("invoice", Ocr.MatchType("\"invoice\"", new List<string> { "invoice" }));
        Assert.Equal("invoice", Ocr.MatchType("(invoice)", new List<string> { "invoice" }));
        Assert.Equal("invoice", Ocr.MatchType("invoice!", new List<string> { "invoice" }));
    }
}
