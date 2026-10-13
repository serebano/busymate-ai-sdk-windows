using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Busymate.AI.Tests;

public class BusymateBridgeTests
{
    private const string Chat = "https://acme.busymate.ai/support/acme?channel=desktop_sdk";
    private static readonly BusymateBridge.HostInfo Host = new() { AppId = "com.acme.desk", AppVersion = "3.1.0", AppBuild = "3.1.0.7", WebView = "130.0.2849.56" };

    private static BusymateBridge Bridge(
        Func<string?>? account = null,
        Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<BusymateBridge.MintResult>>? mint = null,
        Func<string, IReadOnlyDictionary<string, object?>, bool>? onAction = null,
        Action? onClose = null,
        Func<Uri, string, bool>? openUrl = null,
        TimeSpan? ceiling = null) =>
        new(new BusymateBridge.Config
        {
            Assistant = "acme",
            Origins = new[] { "https://support.acme.com", "https://*.acme.com", "http://acme.com" },
            Account = account ?? (() => null),
            Mint = mint ?? ((_, _) => Task.FromResult(BusymateBridge.MintResult.Failed)),
            OnAction = onAction,
            OnClose = onClose,
            OpenUrl = openUrl,
        }, Host, ceiling ?? BusymateBridge.MintCeiling);

    private static string Request(string op, object? p = null, object? id = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["bm"] = 2, ["id"] = id ?? "b1", ["op"] = op, ["p"] = p ?? new { } });

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AllowListIsExactAssistantPlusValidExtras()
    {
        Assert.Equal(new[] { "https://acme.busymate.ai", "https://support.acme.com" }, Bridge().Origins);
    }

    [Fact]
    public async Task HelloReportsTheV2BridgeForWindows()
    {
        var reply = await Bridge().HandleAsync(Request("hello"), Chat);
        Assert.NotNull(reply);
        Assert.Equal("hello", reply!.Operation);
        Assert.True(reply.Ok);
        var root = Parse(reply.Json);
        Assert.Equal(2, root.GetProperty("bm").GetInt32());
        Assert.Equal("b1", root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        var r = root.GetProperty("r");
        Assert.Equal("busymate", r.GetProperty("bridge").GetString());
        Assert.Equal(2, r.GetProperty("v").GetInt32());
        Assert.Equal("2.0.0", r.GetProperty("build").GetString());
        Assert.Equal("windows", r.GetProperty("platform").GetString());
        Assert.Equal("message", r.GetProperty("transport").GetString());
        Assert.Equal("acme", r.GetProperty("assistant").GetString());
        Assert.Equal("https://acme.busymate.ai", r.GetProperty("origin").GetString());
        Assert.Equal(new[] { "https://acme.busymate.ai", "https://support.acme.com" },
            r.GetProperty("origins").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { "hello", "state", "mint", "open", "action", "ev:resume", "ev:state", "ev:restored" },
            r.GetProperty("caps").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("130.0.2849.56", r.GetProperty("webview").GetString());
        Assert.Equal("com.acme.desk", r.GetProperty("app").GetProperty("id").GetString());
        Assert.Equal("3.1.0", r.GetProperty("app").GetProperty("version").GetString());
        Assert.Equal("3.1.0.7", r.GetProperty("app").GetProperty("build").GetString());
    }

    [Fact]
    public async Task NumericIdsAreEchoedVerbatim()
    {
        var reply = await Bridge().HandleAsync(Request("state", id: 42), Chat);
        Assert.Equal(42, Parse(reply!.Json).GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("https://other.busymate.ai/support/other")]
    [InlineData("https://busymate.ai/artifact/x")]
    [InlineData("http://acme.busymate.ai/")]
    [InlineData("https://evil.acme.com/")]
    [InlineData("https://acme.busymate.ai.evil.com/")]
    [InlineData("null")]
    [InlineData(null)]
    public async Task ForeignOriginsAreDeniedAndNeverReachMint(string? source)
    {
        var minted = false;
        var bridge = Bridge(account: () => "u1", mint: (_, _) => { minted = true; return Task.FromResult(BusymateBridge.MintResult.Success("t", "n")); });
        var reply = await bridge.HandleAsync(Request("mint", new { nonce = "n" }), source);
        Assert.NotNull(reply);
        Assert.False(reply!.Ok);
        Assert.Equal("denied", Parse(reply.Json).GetProperty("e").GetString());
        Assert.False(minted);
    }

    [Fact]
    public async Task MappedSupportHostWithDefaultPortIsAllowed()
    {
        var reply = await Bridge().HandleAsync(Request("hello"), "https://SUPPORT.acme.com:443/help");
        Assert.True(reply!.Ok);
        Assert.Equal("https://support.acme.com", Parse(reply.Json).GetProperty("r").GetProperty("origin").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"bm\":1,\"id\":\"x\",\"op\":\"hello\"}")]
    [InlineData("{\"id\":\"x\",\"op\":\"hello\"}")]
    [InlineData("{\"bm\":2,\"op\":\"hello\"}")]
    [InlineData("{\"bm\":2,\"id\":null,\"op\":\"hello\"}")]
    public async Task EnvelopesThatAreNotOursGetNoAnswer(string? data)
    {
        Assert.Null(await Bridge().HandleAsync(data, Chat));
    }

    [Fact]
    public async Task OversizedEnvelopeIsIgnored()
    {
        var big = Request("action", new { name = "ready", @params = new { pad = new string('x', BusymateBridge.MaxEnvelope) } });
        Assert.Null(await Bridge().HandleAsync(big, Chat));
    }

    [Fact]
    public async Task UnknownOpIsUnsupported()
    {
        var reply = await Bridge().HandleAsync(Request("teleport"), Chat);
        Assert.Equal("unsupported", Parse(reply!.Json).GetProperty("e").GetString());
    }

    [Fact]
    public async Task StateHashesTheAccountAndNeverSendsItRaw()
    {
        var signedOut = Parse((await Bridge().HandleAsync(Request("state"), Chat))!.Json).GetProperty("r");
        Assert.False(signedOut.GetProperty("signedIn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, signedOut.GetProperty("account").ValueKind);

        var reply = (await Bridge(account: () => "user-7").HandleAsync(Request("state"), Chat))!.Json;
        var r = Parse(reply).GetProperty("r");
        Assert.True(r.GetProperty("signedIn").GetBoolean());
        var expected = string.Concat(SHA256.HashData(Encoding.UTF8.GetBytes("com.acme.desk:user-7")).Take(16).Select(b => b.ToString("x2")));
        Assert.Equal(expected, r.GetProperty("account").GetString());
        Assert.Equal(32, expected.Length);
        Assert.DoesNotContain("user-7", reply);
    }

    [Fact]
    public async Task AccountReaderThatThrowsIsSignedOut()
    {
        var r = Parse((await Bridge(account: () => throw new InvalidOperationException()).HandleAsync(Request("state"), Chat))!.Json).GetProperty("r");
        Assert.False(r.GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task MintWhenSignedOutIsNotSignedInWithoutCallingBackend()
    {
        var called = false;
        var reply = await Bridge(mint: (_, _) => { called = true; return Task.FromResult(BusymateBridge.MintResult.Failed); })
            .HandleAsync(Request("mint", new { nonce = "n1" }), Chat);
        Assert.Equal("not_signed_in", Parse(reply!.Json).GetProperty("e").GetString());
        Assert.False(called);
    }

    [Fact]
    public async Task MintPassesChatFieldsAndBridgeSetsAssistantAndOrigin()
    {
        IReadOnlyDictionary<string, object?>? seen = null;
        var bridge = Bridge(account: () => "u1", mint: (request, _) =>
        {
            seen = request;
            return Task.FromResult(BusymateBridge.MintResult.Success("jwt", (string)request["nonce"]!));
        });
        var reply = await bridge.HandleAsync(
            Request("mint", new { nonce = "n1", aud = "busymate", reason = "launch", assistant = "evil", origin = "https://evil.com" }), Chat);
        Assert.True(reply!.Ok);
        var r = Parse(reply.Json).GetProperty("r");
        Assert.Equal("jwt", r.GetProperty("token").GetString());
        Assert.Equal("n1", r.GetProperty("nonce").GetString());
        Assert.NotNull(seen);
        Assert.Equal("n1", seen!["nonce"]);
        Assert.Equal("busymate", seen["aud"]);
        Assert.Equal("launch", seen["reason"]);
        Assert.Equal("acme", seen["assistant"]);
        Assert.Equal("https://acme.busymate.ai", seen["origin"]);
    }

    [Fact]
    public async Task MintFailuresMapToWireErrors()
    {
        async Task<string?> Error(Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<BusymateBridge.MintResult>> mint)
        {
            var reply = await Bridge(account: () => "u1", mint: mint).HandleAsync(Request("mint", new { nonce = "n" }), Chat);
            return Parse(reply!.Json).GetProperty("e").GetString();
        }
        Assert.Equal("mint_failed", await Error((_, _) => throw new InvalidOperationException()));
        Assert.Equal("mint_failed", await Error((_, _) => Task.FromException<BusymateBridge.MintResult>(new InvalidOperationException())));
        Assert.Equal("mint_failed", await Error((_, _) => Task.FromResult(BusymateBridge.MintResult.Failed)));
        Assert.Equal("not_signed_in", await Error((_, _) => Task.FromResult(BusymateBridge.MintResult.NotSignedIn)));
    }

    [Fact]
    public async Task MintCeilingAnswersTimeoutAndCancelsTheCallback()
    {
        var cancelled = new TaskCompletionSource<bool>();
        var bridge = Bridge(account: () => "u1", ceiling: TimeSpan.FromMilliseconds(50), mint: async (_, token) =>
        {
            token.Register(() => cancelled.TrySetResult(true));
            await Task.Delay(Timeout.Infinite, token);
            return BusymateBridge.MintResult.Failed;
        });
        var reply = await bridge.HandleAsync(Request("mint", new { nonce = "n" }), Chat);
        Assert.Equal("timeout", Parse(reply!.Json).GetProperty("e").GetString());
        Assert.True(await cancelled.Task);
    }

    [Fact]
    public async Task OpenAcceptsOnlyHttps()
    {
        var opened = new List<(Uri, string)>();
        var bridge = Bridge(openUrl: (uri, mode) => { opened.Add((uri, mode)); return true; });
        var ok = await bridge.HandleAsync(Request("open", new { url = "https://acme.com/login", mode = "auth" }), Chat);
        Assert.True(Parse(ok!.Json).GetProperty("r").GetProperty("opened").GetBoolean());
        Assert.Equal(new Uri("https://acme.com/login"), opened.Single().Item1);
        Assert.Equal("auth", opened.Single().Item2);
        foreach (var url in new[] { "http://acme.com", "javascript:alert(1)", "file:///c:/windows", "ms-settings:privacy", "", "https://" })
        {
            var bad = await bridge.HandleAsync(Request("open", new { url }), Chat);
            Assert.Equal("bad_request", Parse(bad!.Json).GetProperty("e").GetString());
        }
        Assert.Single(opened);
    }

    [Fact]
    public async Task ActionsUseOnActionThenCloseFallback()
    {
        var closed = 0;
        string? seen = null;
        var bridge = Bridge(onAction: (name, _) => { seen = name; return name == "ready"; }, onClose: () => closed++);
        var ready = Parse((await bridge.HandleAsync(Request("action", new { name = "ready" }), Chat))!.Json);
        Assert.True(ready.GetProperty("r").GetProperty("handled").GetBoolean());
        Assert.Equal("ready", seen);
        var close = Parse((await bridge.HandleAsync(Request("action", new { name = "close" }), Chat))!.Json);
        Assert.True(close.GetProperty("r").GetProperty("handled").GetBoolean());
        Assert.Equal(1, closed);
        var unknown = Parse((await bridge.HandleAsync(Request("action", new { name = "launch_missiles" }), Chat))!.Json);
        Assert.False(unknown.GetProperty("r").GetProperty("handled").GetBoolean());
        var tooLong = Parse((await bridge.HandleAsync(Request("action", new { name = new string('a', 65) }), Chat))!.Json);
        Assert.Equal("bad_request", tooLong.GetProperty("e").GetString());
    }

    [Fact]
    public async Task ActionsWithoutHandlersAreNotHandled()
    {
        var close = Parse((await Bridge().HandleAsync(Request("action", new { name = "close" }), Chat))!.Json);
        Assert.False(close.GetProperty("r").GetProperty("handled").GetBoolean());
    }

    [Fact]
    public void EventsUseTheV2EventEnvelope()
    {
        var resume = Parse(Bridge().EventEnvelope("resume"));
        Assert.Equal(2, resume.GetProperty("bm").GetInt32());
        Assert.Equal("resume", resume.GetProperty("ev").GetString());
        Assert.Equal(JsonValueKind.Object, resume.GetProperty("r").ValueKind);
        Assert.False(resume.TryGetProperty("id", out _));
        var state = Parse(Bridge(account: () => "u").EventEnvelope("state")).GetProperty("r");
        Assert.True(state.GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public void ShimDefinesThePortOnlyForAllowedOrigins()
    {
        var script = BusymateWebMessageHub.Shim("BusymateBridge", new[] { "https://acme.busymate.ai" });
        Assert.Contains("var channel = \"BusymateBridge\";", script);
        Assert.Contains("var allowed = [\"https://acme.busymate.ai\"];", script);
        Assert.Contains("self.origin", script);
        Assert.Contains("chrome.webview", script);
        var wrapped = Parse(BusymateWebMessageHub.Wrap("BusymateBridge", "{\"bm\":2}"));
        Assert.Equal("BusymateBridge", wrapped.GetProperty("ch").GetString());
        Assert.Equal("{\"bm\":2}", wrapped.GetProperty("data").GetString());
    }
}
