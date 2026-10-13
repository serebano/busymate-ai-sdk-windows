// Busymate AI — kit v2 native bridge for Windows WebView2 (`busymate.bridge/2`, build 2.0.0).
//
// FROZEN. This file is a pipe, not a policy: it moves one JSON envelope between
// the Busymate chat (served from busymate.ai) and five fixed operations of YOUR
// app. Every message name, timeout, retry, fallback and decision lives in the
// chat, which Busymate updates on its side, so a fix never needs a new build of
// your app. Do not edit it; new bytes are a new SDK version.
//
// Contract: CONTRACT-v2.md (https://busymate.ai/sdk/v2/2.0.0/CONTRACT.md)
//
// Transport: WebView2 WebMessageReceived / PostWebMessageAsJson, wrapped by
// BusymateWebMessageHub so the page sees the same window.BusymateBridge object
// (postMessage(string) + "message" events) as on Android. The sender's origin
// is WebView2's Source for the posting document, never a page-supplied field.
//
// Who may ask: EXACTLY https://<assistant>.busymate.ai plus the exact https
// origins you list (your mapped support host). No wildcards: another tenant's
// chat, or any other busymate.ai page, is answered `denied` and never reaches
// your mint.
//
// Use (before the first Navigate):
//
//   var identity = await BusymateBridge.InstallAsync(webView.CoreWebView2, new BusymateBridge.Config
//   {
//       Assistant = "acme",
//       Origins = new[] { "https://support.acme.com" },          // your mapped support host, if any
//       Account = () => session.UserIdOrNull,                     // null = signed out
//       Mint = (request, ct) => backend.MintBusymateAsync(request, ct),
//   });
//   // `request` also carries "assistant" and "origin", set by this file (never
//   // by the page), so your backend may bind the token to them.
//   identity?.AccountChanged();   // optional: after sign-in / sign-out / switch

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Busymate.AI;

/// <summary>The v2 identity bridge (wire 2, build 2.0.0).</summary>
public sealed class BusymateBridge
{
    public const string Name = "BusymateBridge";
    public const int Version = 2;
    public const string Build = "2.0.0";
    public const string Platform = "windows";
    public const string Transport = "message";
    public const int MaxEnvelope = 64 * 1024;

    /// <summary>The ceiling only reclaims the callback; the chat owns every real budget.</summary>
    public static readonly TimeSpan MintCeiling = TimeSpan.FromSeconds(15);

    private static readonly string[] Caps =
        { "hello", "state", "mint", "open", "action", "ev:resume", "ev:state", "ev:restored" };

    /// <summary>What your mint callback answers with.</summary>
    public sealed class MintResult
    {
        private MintResult(MintResultKind kind, string? token, string? nonce)
        {
            Kind = kind;
            Token = token;
            Nonce = nonce;
        }

        public MintResultKind Kind { get; }
        public string? Token { get; }
        public string? Nonce { get; }

        public static MintResult Success(string token, string nonce) => new(MintResultKind.Token, token, nonce);
        public static MintResult NotSignedIn { get; } = new(MintResultKind.NotSignedIn, null, null);
        public static MintResult Failed { get; } = new(MintResultKind.Failed, null, null);
    }

    public enum MintResultKind { Token, NotSignedIn, Failed }

    public sealed class Config
    {
        /// <summary>Your assistant's slug on busymate.ai; https://&lt;assistant&gt;.busymate.ai may ask.</summary>
        public string Assistant { get; init; } = "";

        /// <summary>Extra EXACT https origins that load the chat (your mapped support host). Wildcards are ignored.</summary>
        public IReadOnlyList<string> Origins { get; init; } = Array.Empty<string>();

        /// <summary>The signed-in account id, or null when signed out. Never leaves the device raw.</summary>
        public Func<string?> Account { get; init; } = () => null;

        /// <summary>Ask YOUR backend for a Busymate launch token: the chat's request, plus "assistant" and "origin" set by this file.</summary>
        public Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<MintResult>> Mint { get; init; } =
            (_, _) => Task.FromResult(MintResult.NotSignedIn);

        /// <summary>Optional: host actions the chat may ask for; return true when handled.</summary>
        public Func<string, IReadOnlyDictionary<string, object?>, bool>? OnAction { get; init; }

        /// <summary>Optional: the chat asked to be closed.</summary>
        public Action? OnClose { get; init; }

        /// <summary>Optional: open an https URL (mode "external" or "auth"); default is the system browser.</summary>
        public Func<Uri, string, bool>? OpenUrl { get; init; }

        /// <summary>Optional: the app id hashed with the account; default is the entry assembly name.</summary>
        public string? AppId { get; init; }
    }

    /// <summary>What hello reports about the host app.</summary>
    public sealed class HostInfo
    {
        public string AppId { get; init; } = "";
        public string? AppVersion { get; init; }
        public string? AppBuild { get; init; }
        public string? WebView { get; init; }

        public static HostInfo FromEntryAssembly(string? appId, string? webView)
        {
            var entry = Assembly.GetEntryAssembly();
            var name = entry?.GetName();
            var informational = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return new HostInfo
            {
                AppId = !string.IsNullOrEmpty(appId) ? appId! : name?.Name ?? "",
                AppVersion = informational ?? name?.Version?.ToString(),
                AppBuild = name?.Version?.ToString(),
                WebView = webView,
            };
        }
    }

    /// <summary>One answer to a page request.</summary>
    public sealed class Reply
    {
        internal Reply(string operation, bool ok, string json)
        {
            Operation = operation;
            Ok = ok;
            Json = json;
        }

        public string Operation { get; }
        public bool Ok { get; }
        public string Json { get; }
    }

    private readonly Config config;
    private readonly HostInfo host;
    private readonly TimeSpan ceiling;

    public BusymateBridge(Config config, HostInfo? host = null) : this(config, host, MintCeiling)
    {
    }

    internal BusymateBridge(Config config, HostInfo? host, TimeSpan ceiling)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.host = host ?? HostInfo.FromEntryAssembly(config.AppId, null);
        this.ceiling = ceiling;
        Origins = AllowedOrigins(config);
    }

    /// <summary>The exact origins allowed to ask.</summary>
    public IReadOnlyList<string> Origins { get; }

    /// <summary>The exact origins allowed to ask: the assistant's own busymate.ai host + your exact https origins.</summary>
    public static IReadOnlyList<string> AllowedOrigins(Config config) =>
        BusymateOrigins.ForAssistant(config.Assistant, config.Origins);

    /// <summary>SHA-256(appId + ":" + account), first 128 bits, lower-case hex. The raw id never crosses.</summary>
    public static string AccountKey(string appId, string account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appId + ":" + account)), 0, 16).ToLowerInvariant();

    // ── the pipe ────────────────────────────────────────────────────────────

    /// <summary>Answers one envelope from <paramref name="source"/> (the posting document's URI). Null: not ours, no answer.</summary>
    public async Task<Reply?> HandleAsync(string? data, string? source)
    {
        if (data is null || data.Length > MaxEnvelope) return null;
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(data);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("bm", out var bm) || bm.ValueKind != JsonValueKind.Number ||
            !bm.TryGetInt32(out var version) || version != Version) return null;
        if (!root.TryGetProperty("id", out var id) || id.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var op = StringOf(root, "op") ?? "";
        var origin = BusymateOrigins.FromUri(source);
        if (origin is null || !Origins.Contains(origin)) return new Reply(op, false, Error(id, "denied"));
        var p = ObjectOf(root, "p");
        switch (op)
        {
            case "hello":
                return new Reply(op, true, Ok(id, w => WriteHello(w, origin)));
            case "state":
                return new Reply(op, true, Ok(id, WriteState));
            case "mint":
                var minted = await MintAsync(id, p, origin).ConfigureAwait(true);
                return new Reply(op, minted.Ok, minted.Json);
            case "open":
                return Open(op, id, p);
            case "action":
                return HandleAction(op, id, p);
            default:
                return new Reply(op, false, Error(id, "unsupported"));
        }
    }

    /// <summary>An event envelope: {"bm":2,"ev":…,"r":{…}}. "state" carries the account state.</summary>
    public string EventEnvelope(string ev)
    {
        return Write(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("bm", Version);
            w.WriteString("ev", ev);
            w.WritePropertyName("r");
            w.WriteStartObject();
            if (ev == "state") WriteState(w);
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    private async Task<(bool Ok, string Json)> MintAsync(JsonElement id, JsonElement p, string origin)
    {
        if (ReadAccount() is null) return (false, Error(id, "not_signed_in"));
        // Who asked is stated by this file, never by the page.
        var request = ToMap(p);
        request["assistant"] = config.Assistant;
        request["origin"] = origin;
        using var cancel = new CancellationTokenSource();
        using var delayCancel = new CancellationTokenSource();
        Task<MintResult> task;
        try
        {
            task = config.Mint(request, cancel.Token) ?? Task.FromResult(MintResult.Failed);
        }
        catch (Exception)
        {
            return (false, Error(id, "mint_failed"));
        }
        var first = await Task.WhenAny(task, Task.Delay(ceiling, delayCancel.Token)).ConfigureAwait(true);
        if (first != task)
        {
            cancel.Cancel();
            return (false, Error(id, "timeout"));
        }
        delayCancel.Cancel();
        MintResult result;
        try
        {
            result = await task.ConfigureAwait(true);
        }
        catch (Exception)
        {
            return (false, Error(id, "mint_failed"));
        }
        switch (result?.Kind)
        {
            case MintResultKind.Token when !string.IsNullOrEmpty(result.Token) && result.Nonce is not null:
                return (true, Ok(id, w =>
                {
                    w.WriteString("token", result.Token);
                    w.WriteString("nonce", result.Nonce);
                }));
            case MintResultKind.NotSignedIn:
                return (false, Error(id, "not_signed_in"));
            default:
                return (false, Error(id, "mint_failed"));
        }
    }

    private Reply Open(string op, JsonElement id, JsonElement p)
    {
        var url = StringOf(p, "url");
        var mode = StringOf(p, "mode") ?? "external";
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
        {
            return new Reply(op, false, Error(id, "bad_request"));
        }
        bool opened;
        try
        {
            opened = (config.OpenUrl ?? OpenWithShell)(uri, mode);
        }
        catch (Exception)
        {
            opened = false;
        }
        return opened
            ? new Reply(op, true, Ok(id, w => w.WriteBoolean("opened", true)))
            : new Reply(op, false, Error(id, "unsupported"));
    }

    private Reply HandleAction(string op, JsonElement id, JsonElement p)
    {
        var name = StringOf(p, "name") ?? "";
        if (name.Length == 0 || name.Length > 64) return new Reply(op, false, Error(id, "bad_request"));
        var parameters = ToMap(ObjectOf(p, "params"));
        bool handled;
        try
        {
            handled = config.OnAction?.Invoke(name, parameters) == true;
            if (!handled && name == "close" && config.OnClose is not null)
            {
                config.OnClose();
                handled = true;
            }
        }
        catch (Exception)
        {
            handled = false;
        }
        return new Reply(op, true, Ok(id, w => w.WriteBoolean("handled", handled)));
    }

    private void WriteHello(Utf8JsonWriter w, string origin)
    {
        w.WriteString("bridge", "busymate");
        w.WriteNumber("v", Version);
        w.WriteString("build", Build);
        w.WriteString("platform", Platform);
        w.WriteString("transport", Transport);
        w.WriteString("assistant", config.Assistant);
        w.WriteString("origin", origin);
        w.WriteStartArray("origins");
        foreach (var allowed in Origins) w.WriteStringValue(allowed);
        w.WriteEndArray();
        w.WriteStartArray("caps");
        foreach (var cap in Caps) w.WriteStringValue(cap);
        w.WriteEndArray();
        WriteNullable(w, "webview", host.WebView);
        w.WriteStartObject("app");
        w.WriteString("id", host.AppId);
        WriteNullable(w, "version", host.AppVersion);
        WriteNullable(w, "build", host.AppBuild);
        w.WriteEndObject();
    }

    private void WriteState(Utf8JsonWriter w)
    {
        var account = ReadAccount();
        w.WriteBoolean("signedIn", account is not null);
        WriteNullable(w, "account", account is null ? null : AccountKey(host.AppId, account));
    }

    private string? ReadAccount()
    {
        try { return config.Account(); } catch (Exception) { return null; }
    }

    private static bool OpenWithShell(Uri uri, string mode)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return true;
    }

    private static string Ok(JsonElement id, Action<Utf8JsonWriter> r) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("bm", Version);
        w.WritePropertyName("id");
        id.WriteTo(w);
        w.WriteBoolean("ok", true);
        w.WritePropertyName("r");
        w.WriteStartObject();
        r(w);
        w.WriteEndObject();
        w.WriteEndObject();
    });

    private static string Error(JsonElement id, string e) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("bm", Version);
        w.WritePropertyName("id");
        id.WriteTo(w);
        w.WriteBoolean("ok", false);
        w.WriteString("e", e);
        w.WriteEndObject();
    });

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) body(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    private static string? StringOf(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static JsonElement ObjectOf(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : default;

    private static Dictionary<string, object?> ToMap(JsonElement obj)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (obj.ValueKind != JsonValueKind.Object) return map;
        foreach (var property in obj.EnumerateObject())
        {
            var v = property.Value;
            map[property.Name] = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => v.Clone(),
            };
        }
        return map;
    }

    // ── WebView2 installation ───────────────────────────────────────────────

    /// <summary>
    /// Installs the bridge on a CoreWebView2 BEFORE its first navigation. Returns null
    /// (inert, the chat runs as a guest) when nothing valid is allowed.
    /// </summary>
    public static async Task<Installation?> InstallAsync(CoreWebView2 core, Config config)
    {
        if (core is null) throw new ArgumentNullException(nameof(core));
        string? webView = null;
        try { webView = core.Environment.BrowserVersionString; } catch (Exception) { /* unknown runtime */ }
        var bridge = new BusymateBridge(config, HostInfo.FromEntryAssembly(config.AppId, webView));
        if (bridge.Origins.Count == 0) return null;
        var hub = BusymateWebMessageHub.For(core);
        var installation = new Installation(bridge, hub);
        await hub.RegisterAsync(Name, bridge.Origins, installation.ReceiveAsync).ConfigureAwait(true);
        return installation;
    }

    /// <summary>One bridge installed on one CoreWebView2.</summary>
    public sealed class Installation
    {
        private readonly BusymateWebMessageHub hub;
        private readonly SynchronizationContext? context;
        private IBusymateMessageTarget? helloTarget;
        private string? helloOrigin;
        private bool restorePending;

        internal Installation(BusymateBridge bridge, BusymateWebMessageHub hub)
        {
            Bridge = bridge;
            this.hub = hub;
            context = SynchronizationContext.Current;
        }

        public BusymateBridge Bridge { get; }

        /// <summary>Optional: tell the chat the signed-in account changed (sign-in, sign-out, switch).</summary>
        public void AccountChanged() => Run(() => Emit("state"));

        /// <summary>The app returned to the foreground.</summary>
        public void Resumed() => Run(() => Emit("resume"));

        /// <summary>The chat was reloaded after its process died; `restored` follows the next hello.</summary>
        public void MarkRestorePending() => Run(() => restorePending = true);

        internal async Task ReceiveAsync(BusymateHubMessage message)
        {
            var reply = await Bridge.HandleAsync(message.Data, message.Source).ConfigureAwait(true);
            if (reply is null) return;
            message.Target.Post(Name, reply.Json, message.Origin);
            if (reply.Operation == "hello" && reply.Ok)
            {
                helloTarget = message.Target;
                helloOrigin = message.Origin;
                if (restorePending)
                {
                    restorePending = false;
                    message.Target.Post(Name, Bridge.EventEnvelope("restored"), message.Origin);
                }
            }
        }

        private void Emit(string ev)
        {
            var target = helloTarget ?? hub.Top;
            var origin = helloTarget is null ? hub.Top.CurrentOrigin : helloOrigin;
            if (origin is null || !Bridge.Origins.Contains(origin)) return;
            target.Post(Name, Bridge.EventEnvelope(ev), origin);
        }

        private void Run(System.Action action)
        {
            if (context is null || SynchronizationContext.Current == context) action();
            else context.Post(_ => action(), null);
        }
    }
}
