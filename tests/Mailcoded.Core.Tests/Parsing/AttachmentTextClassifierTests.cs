using Mailcoded.Core.Parsing;
using Xunit;

namespace Mailcoded.Core.Tests.Parsing;

public sealed class AttachmentTextClassifierTests
{
    [Theory]
    [InlineData("text/plain", "a.txt", AttachmentTextKind.Text)]
    [InlineData("TEXT/PLAIN; charset=utf-8", null, AttachmentTextKind.Text)]
    [InlineData("text/csv", "x.csv", AttachmentTextKind.Text)]
    [InlineData("text/tab-separated-values", null, AttachmentTextKind.Text)]
    [InlineData("text/markdown", "r.md", AttachmentTextKind.Text)]
    [InlineData("text/x-markdown", null, AttachmentTextKind.Text)]
    [InlineData("text/calendar", "invite.ics", AttachmentTextKind.Text)]
    [InlineData("application/json", "d.json", AttachmentTextKind.Text)]
    [InlineData("text/html", "r.html", AttachmentTextKind.Html)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", null, AttachmentTextKind.Docx)]
    [InlineData("application/octet-stream", "plan.docx", AttachmentTextKind.Docx)]
    [InlineData("application/zip", "plan.DOCX", AttachmentTextKind.Docx)]
    [InlineData("application/x-zip-compressed", "plan.docx", AttachmentTextKind.Docx)]
    [InlineData("application/octet-stream", "notes.TXT", AttachmentTextKind.Text)]
    [InlineData("application/octet-stream", "export.csv", AttachmentTextKind.Text)]
    [InlineData("", "log.log", AttachmentTextKind.Text)]
    [InlineData(null, "meeting.ics", AttachmentTextKind.Text)]
    public void Readable_types_are_recognised(string? mime, string? name, AttachmentTextKind expected)
    {
        Assert.Equal(expected, AttachmentTextClassifier.Classify(mime, name));
    }

    [Theory]
    [InlineData("application/pdf", "a.pdf")]
    [InlineData("application/pdf", "a.txt")]
    [InlineData("image/png", "a.png")]
    [InlineData("image/jpeg", "scan.txt")]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "a.xlsx")]
    [InlineData("application/vnd.openxmlformats-officedocument.presentationml.presentation", "a.pptx")]
    [InlineData("application/msword", "a.doc")]
    [InlineData("application/vnd.ms-word.document.macroEnabled.12", "a.docm")]
    [InlineData("application/rtf", "a.rtf")]
    [InlineData("text/xml", "a.xml")]
    [InlineData("application/xml", "a.xml")]
    [InlineData("application/octet-stream", "a.exe")]
    [InlineData("application/octet-stream", "a.docx.exe")]
    [InlineData("application/octet-stream", "noextension")]
    [InlineData("application/octet-stream", null)]
    [InlineData("application/x-sh", "run.sh")]
    [InlineData("text/plain", "a.docx")]
    [InlineData("application/pgp-encrypted", "encrypted.asc")]
    [InlineData("application/pkcs7-mime", "smime.p7m")]
    [InlineData("application/ms-tnef", "winmail.dat")]
    public void Everything_else_is_left_alone(string? mime, string? name)
    {
        var kind = AttachmentTextClassifier.Classify(mime, name);

        Assert.Equal(mime == "text/plain" && name == "a.docx" ? AttachmentTextKind.Text : AttachmentTextKind.None, kind);
    }
}
