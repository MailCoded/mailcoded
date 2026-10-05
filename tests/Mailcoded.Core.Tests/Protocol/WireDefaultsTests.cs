using System.Text.Json;
using Mailcoded.Protocol;
using Xunit;

namespace Mailcoded.Core.Tests.Protocol;

public sealed class WireDefaultsTests
{
    [Fact]
    public void An_omitted_includeSnippet_is_false_and_an_explicit_true_is_kept()
    {
        var omitted = JsonSerializer.Deserialize("""{"query":"x"}""", ProtocolJsonContext.Default.SearchParams)!;
        var explicitTrue = JsonSerializer.Deserialize("""{"query":"x","includeSnippet":true}""", ProtocolJsonContext.Default.SearchParams)!;

        Assert.False(omitted.IncludeSnippet);
        Assert.True(explicitTrue.IncludeSnippet);
        Assert.False(new SearchParams { Query = "x" }.IncludeSnippet);
    }

    [Fact]
    public void An_omitted_fetchIfMissing_is_false_and_an_explicit_true_is_kept()
    {
        var omitted = JsonSerializer.Deserialize("""{"messageId":3}""", ProtocolJsonContext.Default.MessageGetParams)!;
        var explicitTrue = JsonSerializer.Deserialize("""{"messageId":3,"fetchIfMissing":true}""", ProtocolJsonContext.Default.MessageGetParams)!;

        Assert.False(omitted.FetchIfMissing);
        Assert.True(explicitTrue.FetchIfMissing);
        Assert.False(new MessageGetParams { MessageId = 3 }.FetchIfMissing);
    }
}
