using Xunit;

namespace Busymate.AI.Tests;

public class BusymateOriginsTests
{
    [Theory]
    [InlineData("https://acme.busymate.ai", "https://acme.busymate.ai")]
    [InlineData(" HTTPS://Support.Acme.com/ ", "https://support.acme.com")]
    [InlineData("https://support.acme.com:443", "https://support.acme.com")]
    [InlineData("https://support.acme.com:8443", "https://support.acme.com:8443")]
    [InlineData("http://support.acme.com", null)]
    [InlineData("https://*.acme.com", null)]
    [InlineData("https://acme.com/path", null)]
    [InlineData("https://acme.com?x=1", null)]
    [InlineData("https://user@acme.com", null)]
    [InlineData("null", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeAcceptsOnlyExactHttpsOrigins(string? input, string? expected)
    {
        Assert.Equal(expected, BusymateOrigins.Normalize(input));
    }

    [Theory]
    [InlineData("https://acme.busymate.ai/support/acme?channel=desktop_sdk", "https://acme.busymate.ai")]
    [InlineData("https://ACME.busymate.ai:443/x#y", "https://acme.busymate.ai")]
    [InlineData("https://acme.busymate.ai:8443/", "https://acme.busymate.ai:8443")]
    [InlineData("http://acme.busymate.ai/", null)]
    [InlineData("about:blank", null)]
    [InlineData("data:text/html,hi", null)]
    [InlineData("file:///C:/chat.html", null)]
    [InlineData("not a url", null)]
    public void FromUriReadsTheDocumentOrigin(string? uri, string? expected)
    {
        Assert.Equal(expected, BusymateOrigins.FromUri(uri));
    }

    [Theory]
    [InlineData("acme", true)]
    [InlineData("busy-proxy2", true)]
    [InlineData("Acme", false)]
    [InlineData("-acme", false)]
    [InlineData("acme-", false)]
    [InlineData("acme\n", false)]
    [InlineData("ac.me", false)]
    [InlineData("", false)]
    public void AssistantSlugIsStrict(string slug, bool valid)
    {
        Assert.Equal(valid, BusymateOrigins.IsAssistantSlug(slug));
    }

    [Fact]
    public void ForAssistantAddsOnlyItsOwnHostAndValidExtras()
    {
        var list = BusymateOrigins.ForAssistant("acme", new[] { "https://support.acme.com/", "https://SUPPORT.acme.com", "https://*.busymate.ai", "https://busymate.ai/x" });
        Assert.Equal(new[] { "https://acme.busymate.ai", "https://support.acme.com" }, list);
        Assert.Empty(BusymateOrigins.ForAssistant("Not A Slug", null));
    }
}
