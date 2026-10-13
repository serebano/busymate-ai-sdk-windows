// Busymate AI — the WebView2 transport shared by BusymateBridge and BusymateMicrophone.
//
// WebView2 has one page channel (window.chrome.webview). This file gives each
// Busymate bridge the same page-side shape the Android SDK gets from
// WebViewCompat.addWebMessageListener: an object window.<Name> with
// postMessage(string) whose replies arrive as "message" events on the object.
//
//   page → app:  chrome.webview.postMessage({ch:"<Name>", data:"<string>"})
//   app → page:  PostWebMessageAsJson({"ch":"<Name>","data":"<string>"})
//
// The object is only defined in documents whose own origin (self.origin) is in
// that bridge's exact allow-list, and the app re-checks the sender's origin from
// WebView2's Source on every message: the page never states who it is.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Busymate.AI;

/// <summary>One message received on a Busymate channel.</summary>
internal sealed class BusymateHubMessage
{
    internal BusymateHubMessage(string data, string? source, IBusymateMessageTarget target)
    {
        Data = data;
        Source = source;
        Origin = BusymateOrigins.FromUri(source);
        Target = target;
    }

    public string Data { get; }
    public string? Source { get; }
    public string? Origin { get; }
    public IBusymateMessageTarget Target { get; }
}

/// <summary>Where a reply or event goes: the top document or one frame.</summary>
internal interface IBusymateMessageTarget
{
    /// <summary>Posts when the target still shows <paramref name="expectedOrigin"/>; false otherwise.</summary>
    bool Post(string channel, string data, string? expectedOrigin);

    /// <summary>The target's current document origin, when known.</summary>
    string? CurrentOrigin { get; }
}

internal sealed class BusymateWebMessageHub
{
    /// <summary>Upper bound for one wrapped message (a 64 KB envelope, JSON-escaped).</summary>
    internal const int MaxWrapped = 256 * 1024;

    private static readonly ConditionalWeakTable<CoreWebView2, BusymateWebMessageHub> Hubs = new();

    private readonly CoreWebView2 core;
    private readonly Dictionary<string, Func<BusymateHubMessage, Task>> handlers = new(StringComparer.Ordinal);

    private BusymateWebMessageHub(CoreWebView2 core)
    {
        this.core = core;
        Top = new TopTarget(core);
        core.WebMessageReceived += (_, e) => Dispatch(JsonOf(e), SourceOf(e), Top);
        core.FrameCreated += (_, e) => AttachFrame(e.Frame);
    }

    public IBusymateMessageTarget Top { get; }

    public static BusymateWebMessageHub For(CoreWebView2 core) => Hubs.GetValue(core, c => new BusymateWebMessageHub(c));

    /// <summary>Registers a channel and injects its page object for future documents. Call before the first navigation.</summary>
    public async Task RegisterAsync(string channel, IReadOnlyList<string> origins, Func<BusymateHubMessage, Task> handler)
    {
        handlers[channel] = handler;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Shim(channel, origins)).ConfigureAwait(true);
    }

    private void AttachFrame(CoreWebView2Frame frame)
    {
        var target = new FrameTarget(frame);
        try
        {
            frame.WebMessageReceived += (_, e) =>
            {
                var source = SourceOf(e);
                target.Seen(source);
                Dispatch(JsonOf(e), source, target);
            };
            frame.NavigationStarting += (_, e) => target.Navigating(e.Uri);
            frame.Destroyed += (_, _) => target.Destroy();
        }
        catch (Exception)
        {
            // Older runtimes without frame messaging: the top document still works.
        }
    }

    private async void Dispatch(string? json, string? source, IBusymateMessageTarget target)
    {
        try
        {
            if (json is null || json.Length > MaxWrapped) return;
            string? channel;
            string? data;
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                channel = root.TryGetProperty("ch", out var ch) && ch.ValueKind == JsonValueKind.String ? ch.GetString() : null;
                data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            }
            if (channel is null || data is null) return;
            if (!handlers.TryGetValue(channel, out var handler)) return;
            await handler(new BusymateHubMessage(data, source, target)).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A malformed page message never reaches, or crashes, the host app.
        }
    }

    private static string? JsonOf(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try { return e.WebMessageAsJson; } catch (Exception) { return null; }
    }

    private static string? SourceOf(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try { return e.Source; } catch (Exception) { return null; }
    }

    /// <summary>{"ch":channel,"data":data}</summary>
    internal static string Wrap(string channel, string data)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("ch", channel);
            writer.WriteString("data", data);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The document-created script that defines window.&lt;channel&gt; on allowed origins only.</summary>
    internal static string Shim(string channel, IReadOnlyList<string> origins)
    {
        var name = JsonSerializer.Serialize(channel);
        var allowed = JsonSerializer.Serialize(origins);
        return "(function () {\n" +
               "  \"use strict\";\n" +
               "  var channel = " + name + ";\n" +
               "  var allowed = " + allowed + ";\n" +
               "  var origin;\n" +
               "  try { origin = String(self.origin); } catch (e) { return; }\n" +
               "  if (allowed.indexOf(origin) < 0) return;\n" +
               "  var host = window.chrome && window.chrome.webview;\n" +
               "  if (!host || typeof host.postMessage !== \"function\" || window[channel]) return;\n" +
               "  var listeners = [];\n" +
               "  var port = {\n" +
               "    onmessage: null,\n" +
               "    postMessage: function (value) { host.postMessage({ ch: channel, data: String(value) }); },\n" +
               "    addEventListener: function (type, listener) {\n" +
               "      if (type === \"message\" && typeof listener === \"function\" && listeners.indexOf(listener) < 0) listeners.push(listener);\n" +
               "    },\n" +
               "    removeEventListener: function (type, listener) {\n" +
               "      var i = listeners.indexOf(listener);\n" +
               "      if (type === \"message\" && i >= 0) listeners.splice(i, 1);\n" +
               "    }\n" +
               "  };\n" +
               "  host.addEventListener(\"message\", function (event) {\n" +
               "    var m = event && event.data;\n" +
               "    if (!m || m.ch !== channel || typeof m.data !== \"string\") return;\n" +
               "    var message = { data: m.data };\n" +
               "    listeners.slice().forEach(function (l) { try { l(message); } catch (e) {} });\n" +
               "    if (typeof port.onmessage === \"function\") { try { port.onmessage(message); } catch (e) {} }\n" +
               "  });\n" +
               "  try { Object.defineProperty(window, channel, { value: port, configurable: false, enumerable: false, writable: false }); } catch (e) {}\n" +
               "})();\n";
    }

    private sealed class TopTarget : IBusymateMessageTarget
    {
        private readonly CoreWebView2 core;

        public TopTarget(CoreWebView2 core) => this.core = core;

        public string? CurrentOrigin
        {
            get
            {
                try { return BusymateOrigins.FromUri(core.Source); } catch (Exception) { return null; }
            }
        }

        public bool Post(string channel, string data, string? expectedOrigin)
        {
            try
            {
                // The top document may have navigated while the app was answering.
                if (expectedOrigin is not null && CurrentOrigin != expectedOrigin) return false;
                core.PostWebMessageAsJson(Wrap(channel, data));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private sealed class FrameTarget : IBusymateMessageTarget
    {
        private readonly CoreWebView2Frame frame;
        private string? origin;
        private bool destroyed;

        public FrameTarget(CoreWebView2Frame frame) => this.frame = frame;

        public string? CurrentOrigin => destroyed ? null : origin;

        public void Seen(string? source) => origin = BusymateOrigins.FromUri(source);

        public void Navigating(string? uri) => origin = BusymateOrigins.FromUri(uri);

        public void Destroy() => destroyed = true;

        public bool Post(string channel, string data, string? expectedOrigin)
        {
            try
            {
                if (destroyed) return false;
                if (expectedOrigin is not null && origin != expectedOrigin) return false;
                frame.PostWebMessageAsJson(Wrap(channel, data));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
