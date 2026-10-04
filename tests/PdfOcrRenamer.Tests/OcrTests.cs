using System.Collections.Generic;
using PdfOcrRenamer;

namespace PdfOcrRenamer.Tests
{
    public class OcrTests
    {
        [Fact]
        public void ParseAnswer_ParsesTypeAndSender()
        {
            var parsed = Ocr.ParseAnswer("type: invoice\nsender: ACME GmbH", new List<string> { "invoice", "letter" });
            Assert.Equal("invoice", parsed.Matched);
            Assert.Equal("ACME GmbH", parsed.Sender);
        }

        [Fact]
        public void ParseAnswer_UnknownSenderBecomesEmpty()
        {
            var parsed = Ocr.ParseAnswer("type: invoice\nsender: unknown", new List<string> { "invoice" });
            Assert.Equal("", parsed.Sender);
        }

        [Fact]
        public void ParseAnswer_ExtraLinesIgnored()
        {
            var parsed = Ocr.ParseAnswer("Here is my answer:\ntype: receipt\nsender: Foo Inc\nthanks!", new List<string> { "receipt" });
            Assert.Equal("receipt", parsed.Matched);
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
}
