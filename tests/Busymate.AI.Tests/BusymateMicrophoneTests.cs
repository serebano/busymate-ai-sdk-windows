using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using static Busymate.AI.BusymateMicrophone;

namespace Busymate.AI.Tests;

public class BusymateMicrophoneTests
{
    private const string Chat = "https://acme.busymate.ai/support/acme";

    private sealed class Fixture
    {
        public MicrophonePrivacy Privacy = MicrophonePrivacy.Allowed;
        public DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public Func<string, string, Task<bool>>? Confirm;

        public BusymateMicrophone Create() => new(new Options
        {
            Origins = new[] { "https://acme.busymate.ai", "https://*.evil.com", "http://acme.com" },
            Privacy = () => Privacy,
            Clock = () => Now,
            Confirm = Confirm,
        });
    }

    private static string Request(string id = "mic-1", string source = "dictation", string type = RequestType) =>
        JsonSerializer.Serialize(new { type, id, source });

    private static (string? Id, bool Granted) Parse(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        return (root.GetProperty("id").GetString(), root.GetProperty("granted").GetBoolean());
    }

    [Fact]
    public void OnlyExactHttpsOriginsAreKept()
    {
        Assert.Equal(new[] { "https://acme.busymate.ai" }, new Fixture().Create().Origins);
    }

    [Fact]
    public async Task TapRequestFromAllowedOriginIsGrantedAndCorrelated()
    {
        var reply = await new Fixture().Create().HandleAsync(Request("mic-abc"), Chat);
        Assert.Equal(("mic-abc", true), Parse(reply!));
    }

    [Theory]
    [InlineData("https://other.busymate.ai/")]
    [InlineData("https://busymate.ai/")]
    [InlineData("http://acme.busymate.ai/")]
    [InlineData(null)]
    public async Task ForeignOriginsGetNoReply(string? source)
    {
        Assert.Null(await new Fixture().Create().HandleAsync(Request(), source));
    }

    [Theory]
    [InlineData("{\"type\":\"busymate.microphone.v2.request\",\"id\":\"x\"}")]
    [InlineData("{\"type\":\"busymate.microphone.v1.request\",\"id\":\"\"}")]
    [InlineData("{\"type\":\"busymate.microphone.v1.request\"}")]
    [InlineData("{\"type\":\"busymate.microphone.v1.request\",\"id\":7}")]
    [InlineData("[]")]
    [InlineData("nope")]
    public async Task MalformedRequestsGetNoReply(string data)
    {
        Assert.Null(await new Fixture().Create().HandleAsync(data, Chat));
    }

    [Fact]
    public async Task OversizedRequestIsIgnored()
    {
        Assert.Null(await new Fixture().Create().HandleAsync(Request(new string('x', MaxMessage)), Chat));
    }

    [Theory]
    [InlineData(MicrophonePrivacy.DeniedForDevice)]
    [InlineData(MicrophonePrivacy.DeniedForApps)]
    [InlineData(MicrophonePrivacy.DeniedForDesktopApps)]
    public async Task WindowsPrivacyDenialRepliesNotGranted(MicrophonePrivacy privacy)
    {
        var fixture = new Fixture { Privacy = privacy };
        var microphone = fixture.Create();
        Assert.Equal(("mic-1", false), Parse((await microphone.HandleAsync(Request(), Chat))!));
        Assert.Equal(MediaDecision.Deny, microphone.Decide(MediaKind.Microphone, Chat));
    }

    [Fact]
    public async Task UnknownPrivacyStateIsLeftToWindows()
    {
        var fixture = new Fixture { Privacy = MicrophonePrivacy.Unknown };
        Assert.True(Parse((await fixture.Create().HandleAsync(Request(), Chat))!).Granted);
    }

    [Fact]
    public async Task HostDeclineAndRetry()
    {
        var answers = new Queue<bool>(new[] { false, true });
        var fixture = new Fixture();
        fixture.Confirm = (_, _) => Task.FromResult(answers.Dequeue());
        var microphone = fixture.Create();
        Assert.False(Parse((await microphone.HandleAsync(Request("a"), Chat))!).Granted);
        Assert.Equal(MediaDecision.Deny, microphone.Decide(MediaKind.Microphone, Chat));
        // "Try again" asks again.
        Assert.True(Parse((await microphone.HandleAsync(Request("b"), Chat))!).Granted);
        Assert.Equal(MediaDecision.Allow, microphone.Decide(MediaKind.Microphone, Chat));
    }

    [Fact]
    public async Task ConfirmThatThrowsIsADenial()
    {
        var fixture = new Fixture { Confirm = (_, _) => throw new InvalidOperationException() };
        Assert.False(Parse((await fixture.Create().HandleAsync(Request(), Chat))!).Granted);
    }

    [Fact]
    public async Task ConcurrentRequestsShareOnePendingAsk()
    {
        var gate = new TaskCompletionSource<bool>();
        var asks = 0;
        var fixture = new Fixture { Confirm = (_, _) => { asks++; return gate.Task; } };
        var microphone = fixture.Create();
        var first = microphone.HandleAsync(Request("one"), Chat);
        var second = microphone.HandleAsync(Request("two", "voice"), Chat);
        gate.SetResult(true);
        Assert.Equal(("one", true), Parse((await first)!));
        Assert.Equal(("two", true), Parse((await second)!));
        Assert.Equal(1, asks);
        // A later activation re-checks: a new ask.
        await microphone.HandleAsync(Request("three"), Chat);
        Assert.Equal(2, asks);
    }

    [Fact]
    public async Task WebView2DecisionNeedsTheTapAndTheAllowedOrigin()
    {
        var fixture = new Fixture();
        var microphone = fixture.Create();
        // Opening chat never grants: no tap yet.
        Assert.Equal(MediaDecision.Deny, microphone.Decide(MediaKind.Microphone, Chat));
        await microphone.HandleAsync(Request(), Chat);
        Assert.Equal(MediaDecision.Allow, microphone.Decide(MediaKind.Microphone, Chat));
        Assert.Equal(MediaDecision.Deny, microphone.Decide(MediaKind.Microphone, "https://other.busymate.ai/"));
        Assert.Equal(MediaDecision.Deny, microphone.Decide(MediaKind.Camera, Chat));
        Assert.Equal(MediaDecision.Default, microphone.Decide(MediaKind.Other, Chat));
    }

    [Fact]
    public async Task GrantWindowExpires()
    {
        var fixture = new Fixture();
        var microphone = fixture.Create();
        await microphone.HandleAsync(Request(), Chat);
        fixture.Now += GrantWindow + TimeSpan.FromSeconds(1);
        Assert.Equal(MediaDecision.Deny, microphone.Decide(MediaKind.Microphone, Chat));
    }

    [Fact]
    public async Task RevokedInWindowsSettingsIsSeenImmediately()
    {
        var fixture = new Fixture();
        var microphone = fixture.Create();
        Assert.True(Parse((await microphone.HandleAsync(Request("a"), Chat))!).Granted);
        fixture.Privacy = MicrophonePrivacy.DeniedForDesktopApps;
        Assert.Equal(MediaDecision.Deny, microphone.Decide(MediaKind.Microphone, Chat));
        Assert.False(Parse((await microphone.HandleAsync(Request("b"), Chat))!).Granted);
    }

    [Fact]
    public void ReplyIsTheWireOneShape()
    {
        using var document = JsonDocument.Parse(ReplyJson("mic-9", true));
        var root = document.RootElement;
        Assert.Equal(2, CountProperties(root));
        Assert.Equal("mic-9", root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("granted").GetBoolean());
        Assert.Equal("busymate.microphone.v1.request", RequestType);
        Assert.Equal("BusymateMicrophone", Name);
    }

    private static int CountProperties(JsonElement element)
    {
        var count = 0;
        foreach (var _ in element.EnumerateObject()) count++;
        return count;
    }
}
