using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Busymate.AI.Example;
using Xunit;

namespace Busymate.AI.Tests;

public class ExampleAppTests
{
    [Fact]
    public void DefaultsAreTheRealGuestDesktopChat()
    {
        var settings = new DemoSettings();
        Assert.Null(settings.ValidationError());
        Assert.False(settings.IdentityEnabled);
        Assert.Equal("https://busymate.ai/support/busyproxy?channel=desktop&locale=en", settings.EffectiveChatUrl().AbsoluteUri);
        var options = settings.ChatOptions((_, _) => Task.FromResult(BusymateBridge.MintResult.Failed), _ => { }, () => { }, null);
        Assert.Null(options.Identity);
        Assert.NotNull(options.Microphone);
        Assert.Contains("https://busymate.ai", BusymateChat.TrustedOriginsOf(options));
        Assert.Contains("https://busyproxy.busymate.ai", BusymateChat.TrustedOriginsOf(options));
    }

    [Fact]
    public void LocaleSettingReplacesTheQueryLocale()
    {
        var settings = new DemoSettings { Locale = "pt-BR" };
        Assert.Equal("https://busymate.ai/support/busyproxy?channel=desktop&locale=pt-BR", settings.EffectiveChatUrl().AbsoluteUri);
    }

    [Theory]
    [InlineData("Bad Slug", null, null, null, "Use a valid assistant slug.")]
    [InlineData(null, "http://busymate.ai/support/busyproxy", null, null, "Chat URL must use HTTPS without credentials.")]
    [InlineData(null, "https://user:pw@busymate.ai/support", null, null, "Chat URL must use HTTPS without credentials.")]
    [InlineData(null, null, "https://*.busymate.ai", null, "Origins must be exact HTTPS scheme + host + optional port.")]
    [InlineData(null, null, "https://busymate.ai/support", null, "Origins must be exact HTTPS scheme + host + optional port.")]
    [InlineData(null, null, "http://busymate.ai", null, "Origins must be exact HTTPS scheme + host + optional port.")]
    [InlineData(null, null, null, "en_US!", "Locale must look like en or pt-BR.")]
    public void SettingsBoundaries(string? assistant, string? chatUrl, string? origins, string? locale, string expected)
    {
        var defaults = new DemoSettings();
        var settings = defaults with
        {
            Assistant = assistant ?? defaults.Assistant,
            ChatUrl = chatUrl ?? defaults.ChatUrl,
            ExtraOrigins = origins ?? defaults.ExtraOrigins,
            Locale = locale ?? defaults.Locale,
        };
        Assert.Equal(expected, settings.ValidationError());
    }

    [Fact]
    public void IdentityModeNeedsAnHttpsBackend()
    {
        Assert.Equal("Identity mode needs your authenticated backend mint URL.", new DemoSettings { IdentityEnabled = true }.ValidationError());
        Assert.Equal("Backend URL must use HTTPS.", new DemoSettings { IdentityEnabled = true, BackendUrl = "http://api.acme.com/mint" }.ValidationError());
        Assert.Null(new DemoSettings { IdentityEnabled = true, BackendUrl = "https://api.acme.com/mint" }.ValidationError());
    }

    [Fact]
    public void CallbackTogglesReachTheRealSdkConfig()
    {
        var log = new List<string>();
        var closed = 0;
        var on = new DemoSettings { IdentityEnabled = true, BackendUrl = "https://api.acme.com/mint", AccountId = "u1" }
            .IdentityConfig((_, _) => Task.FromResult(BusymateBridge.MintResult.Failed), log.Add, () => closed++)!;
        Assert.NotNull(on.OnAction);
        Assert.NotNull(on.OnClose);
        Assert.True(on.OnAction!("ready", new Dictionary<string, object?>()));
        on.OnClose!();
        Assert.Equal(1, closed);

        var off = new DemoSettings { IdentityEnabled = true, BackendUrl = "https://api.acme.com/mint", CloseEnabled = false, ActionsEnabled = false }
            .IdentityConfig((_, _) => Task.FromResult(BusymateBridge.MintResult.Failed), log.Add, () => closed++)!;
        Assert.Null(off.OnAction);
        Assert.Null(off.OnClose);
        Assert.Null(new DemoSettings().IdentityConfig((_, _) => Task.FromResult(BusymateBridge.MintResult.Failed), log.Add, () => { }));
    }

    [Fact]
    public void AccountIsReadOnlyWhileSignedIn()
    {
        var config = new DemoSettings { IdentityEnabled = true, BackendUrl = "https://api.acme.com/mint", AccountId = "u1" }
            .IdentityConfig((_, _) => Task.FromResult(BusymateBridge.MintResult.Failed), _ => { }, () => { })!;
        DemoSession.SignedIn = false;
        Assert.Null(config.Account());
        DemoSession.SignedIn = true;
        try { Assert.Equal("u1", config.Account()); }
        finally { DemoSession.SignedIn = false; }
    }

    [Fact]
    public void BackendReplyMustEchoTheRequestedNonce()
    {
        var ok = BackendMint.Reply("jwt", "n1", "n1");
        Assert.Equal(BusymateBridge.MintResultKind.Token, ok.Kind);
        Assert.Throws<InvalidOperationException>(() => BackendMint.Reply("jwt", "n2", "n1"));
        Assert.Throws<InvalidOperationException>(() => BackendMint.Reply("", "n1", "n1"));
    }

    [Fact]
    public void BackendClientRefusesRedirects()
    {
        using var handler = BackendMint.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
        public HttpRequestMessage? Seen;
        public string? Body;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static readonly DemoSettings Configured = new() { IdentityEnabled = true, BackendUrl = "https://api.acme.com/mint", AccountId = "u1", BackendBearer = "session" };
    private static readonly Dictionary<string, object?> MintRequest = new() { ["nonce"] = "n1", ["assistant"] = "busyproxy", ["origin"] = "https://busymate.ai" };

    [Fact]
    public async Task BackendMintPostsTheSdkRequestWithTheAppSession()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"token\":\"jwt\",\"nonce\":\"n1\"}", Encoding.UTF8, "application/json"),
        });
        var result = await BackendMint.MintAsync(Configured, true, MintRequest, CancellationToken.None, new HttpClient(stub));
        Assert.Equal(BusymateBridge.MintResultKind.Token, result.Kind);
        Assert.Equal("jwt", result.Token);
        Assert.Equal("Bearer", stub.Seen!.Headers.Authorization!.Scheme);
        Assert.Contains("\"nonce\":\"n1\"", stub.Body);
        Assert.Contains("\"assistant\":\"busyproxy\"", stub.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BusymateBridge.MintResultKind.NotSignedIn)]
    [InlineData(HttpStatusCode.Found, BusymateBridge.MintResultKind.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, BusymateBridge.MintResultKind.Failed)]
    public async Task BackendMintMapsStatuses(HttpStatusCode status, BusymateBridge.MintResultKind expected)
    {
        var stub = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://elsewhere.example/mint");
            return response;
        });
        var result = await BackendMint.MintAsync(Configured, true, MintRequest, CancellationToken.None, new HttpClient(stub));
        Assert.Equal(expected, result.Kind);
    }

    [Fact]
    public async Task SignedOutNeverCallsTheBackend()
    {
        var stub = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var result = await BackendMint.MintAsync(Configured, false, MintRequest, CancellationToken.None, new HttpClient(stub));
        Assert.Equal(BusymateBridge.MintResultKind.NotSignedIn, result.Kind);
        Assert.Null(stub.Seen);
    }
}
