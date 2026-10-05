using System.Text;
using Mailcoded.Core.Domain.Search;
using Mailcoded.Core.Store;

namespace Mailcoded.Integration.Support;

public static class SignalMail
{
    public static string[] Subjects(MailHarness harness, string query, CancellationToken ct)
    {
        var hits = harness.Store.Search(
            new StoreSearchRequest { Query = SearchQueryParser.Parse(query).Query, Order = SearchOrder.Date, Limit = 50 },
            ct).Hits;

        var subjects = new List<string>();
        foreach (var hit in hits) subjects.Add(hit.Subject ?? string.Empty);
        subjects.Sort(StringComparer.Ordinal);
        return [.. subjects];
    }

    public static byte[] Raw(string subject, string[] headers, string body = "body")
    {
        var builder = new StringBuilder();
        builder.Append("From: Sender <sender@example.com>\r\nTo: recipient@example.org\r\n");
        builder.Append("Subject: ").Append(subject).Append("\r\nDate: Mon, 05 Jan 2026 09:00:00 +0000\r\nMIME-Version: 1.0\r\n");
        foreach (var header in headers) builder.Append(header).Append("\r\n");
        builder.Append("\r\n").Append(body).Append("\r\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
