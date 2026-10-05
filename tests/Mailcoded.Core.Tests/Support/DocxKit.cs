using System.IO.Compression;
using System.Text;

namespace Mailcoded.Core.Tests.Support;

/// <summary>Crafts .docx containers and the MIME messages that carry them, so a test names exactly what is hostile about its input.</summary>
public static class DocxKit
{
    public const string Wml = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

    public static string Document(params string[] paragraphs)
    {
        var body = new StringBuilder();
        foreach (var paragraph in paragraphs) body.Append("<w:p><w:r><w:t>").Append(paragraph).Append("</w:t></w:r></w:p>");
        return Wrap(body.ToString());
    }

    public static string Wrap(string body) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document " + Wml + "><w:body>" + body + "</w:body></w:document>";

    public static byte[] Zip(params (string Name, byte[] Data)[] entries) => Zip(CompressionLevel.Optimal, entries);

    public static byte[] Zip(CompressionLevel level, params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name, level);
                using var sink = entry.Open();
                sink.Write(data, 0, data.Length);
            }
        }

        return stream.ToArray();
    }

    public static byte[] Docx(string documentXml, params (string Name, string Xml)[] more)
    {
        var entries = new List<(string, byte[])>
        {
            ("[Content_Types].xml", Encoding.UTF8.GetBytes("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>")),
            ("word/document.xml", Encoding.UTF8.GetBytes(documentXml)),
        };

        foreach (var (name, xml) in more) entries.Add((name, Encoding.UTF8.GetBytes(xml)));
        return Zip([.. entries]);
    }

    public static byte[] Paragraphs(params string[] paragraphs) => Docx(Document(paragraphs));

    /// <summary>A multipart/mixed message with a text body and the given attachments.</summary>
    public static byte[] Message(string subject, params Part[] parts)
    {
        var sb = new StringBuilder();
        sb.Append("From: Sender <sender@example.com>\r\n");
        sb.Append("To: Reader <reader@example.org>\r\n");
        sb.Append("Subject: ").Append(subject).Append("\r\n");
        sb.Append("Date: Tue, 20 Jan 2026 08:00:00 +0000\r\n");
        sb.Append("Message-ID: <attachment-text-").Append(Guid.NewGuid().ToString("N")).Append("@example.com>\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("Content-Type: multipart/mixed; boundary=\"b1\"\r\n\r\n");
        sb.Append("--b1\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nSee the attachment.\r\n");

        foreach (var part in parts)
        {
            sb.Append("--b1\r\n");
            sb.Append("Content-Type: ").Append(part.ContentType).Append("\r\n");
            if (part.FileName is not null)
                sb.Append("Content-Disposition: attachment; filename=\"").Append(part.FileName).Append("\"\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Base64Lines(part.Data)).Append("\r\n");
        }

        sb.Append("--b1--\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public static Part Attach(string contentType, string? fileName, byte[] data) => new(contentType, fileName, data);

    public static Part Text(string contentType, string? fileName, string text) =>
        new(contentType, fileName, Encoding.UTF8.GetBytes(text));

    public static Part DocxPart(byte[] docx, string fileName = "notes.docx") =>
        new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName, docx);

    public static string Base64Lines(byte[] data)
    {
        var encoded = Convert.ToBase64String(data);
        var sb = new StringBuilder(encoded.Length + (encoded.Length / 76 * 2) + 2);
        for (var i = 0; i < encoded.Length; i += 76)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append(encoded, i, Math.Min(76, encoded.Length - i));
        }

        return sb.ToString();
    }

    public sealed record Part(string ContentType, string? FileName, byte[] Data);
}
