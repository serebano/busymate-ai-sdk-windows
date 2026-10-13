// Busymate AI — native microphone adapter for Windows WebView2 (wire 1, adapter 1.0.0).
//
// Optional add-on, independent of the frozen identity bridge. Install once per
// CoreWebView2 before the first navigation and keep it for the WebView's life.
//
// The chat posts {"type":"busymate.microphone.v1.request","id":…,"source":"dictation"|"voice"}
// on window.BusymateMicrophone ONLY when the user taps its microphone or voice
// control. The adapter then:
//   1. checks the sender's origin (WebView2 Source) against the exact allow-list;
//   2. re-reads the Windows microphone privacy settings (device, apps, desktop
//      apps) on every request, so a grant revoked in Settings is seen at once;
//   3. optionally asks your own Confirm callback (a native consent dialog);
//   4. replies {"id":…,"granted":true|false}; concurrent requests share one ask.
// CoreWebView2.PermissionRequested is the second decision: the adapter allows
// ONLY Microphone, ONLY for an allowed requesting origin, ONLY within the window
// opened by that tap-driven grant, and ONLY while Windows privacy still allows
// it. Camera is denied. Other permission kinds are left to your app.
// Opening chat never asks; the adapter never opens Settings by itself.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Busymate.AI;

/// <summary>The microphone adapter (wire 1, adapter 1.0.0).</summary>
public sealed class BusymateMicrophone
{
    public const string Name = "BusymateMicrophone";
    public const string RequestType = "busymate.microphone.v1.request";
    public const int Wire = 1;
    public const string AdapterVersion = "1.0.0";
    public const int MaxMessage = 4096;

    /// <summary>How long a tap-driven grant lets the allowed origin start capture (the chat's own wait is 60 s).</summary>
    public static readonly TimeSpan GrantWindow = TimeSpan.FromSeconds(60);

    public enum MediaKind { Microphone, Camera, Other }

    public enum MediaDecision { Default, Allow, Deny }

    public sealed class Options
    {
        /// <summary>Exact https origins serving the chat (scheme + host + optional port).</summary>
        public IReadOnlyList<string> Origins { get; init; } = Array.Empty<string>();

        /// <summary>Optional native consent after the user's tap: (origin, source) → allow. Default: the tap itself is consent.</summary>
        public Func<string, string, Task<bool>>? Confirm { get; init; }

        /// <summary>Reads Windows microphone privacy. Default: <see cref="WindowsMicrophonePrivacy.Read"/>.</summary>
        public Func<MicrophonePrivacy>? Privacy { get; init; }

        /// <summary>Clock for the grant window (tests).</summary>
        public Func<DateTimeOffset>? Clock { get; init; }

        /// <summary>Optional diagnostics. Never receives audio, tokens or identity.</summary>
        public Action<string>? Log { get; init; }
    }

    private readonly Options options;
    private readonly Dictionary<string, DateTimeOffset> armed = new(StringComparer.Ordinal);
    private Task<bool>? pending;

    public BusymateMicrophone(Options options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        Origins = BusymateOrigins.Exact(options.Origins);
    }

    /// <summary>The normalized exact origins allowed to ask.</summary>
    public IReadOnlyList<string> Origins { get; }

    private DateTimeOffset Now => options.Clock?.Invoke() ?? DateTimeOffset.UtcNow;

    private MicrophonePrivacy ReadPrivacy()
    {
        try { return (options.Privacy ?? WindowsMicrophonePrivacy.Read)(); }
        catch (Exception) { return MicrophonePrivacy.Unknown; }
    }

    /// <summary>Answers one request from <paramref name="source"/>. Null: not a valid request from an allowed origin; no reply.</summary>
    public async Task<string?> HandleAsync(string? data, string? source)
    {
        if (data is null || data.Length > MaxMessage) return null;
        var origin = BusymateOrigins.FromUri(source);
        if (origin is null || !Origins.Contains(origin)) return null;
        string? id;
        string sourceKind;
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() != RequestType) return null;
            id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            sourceKind = root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(id)) return null;
        var granted = await AskAsync(origin, sourceKind).ConfigureAwait(true);
        return ReplyJson(id, granted);
    }

    /// <summary>{"id":id,"granted":granted}</summary>
    internal static string ReplyJson(string id, bool granted)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteBoolean("granted", granted);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Concurrent requests share one pending ask; every later activation re-checks.</summary>
    private async Task<bool> AskAsync(string origin, string source)
    {
        if (pending is { } shared) return await shared.ConfigureAwait(true);
        var task = AskOnceAsync(origin, source);
        pending = task;
        try
        {
            return await task.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(pending, task)) pending = null;
        }
    }

    private async Task<bool> AskOnceAsync(string origin, string source)
    {
        var privacy = ReadPrivacy();
        if (!privacy.Allows())
        {
            armed.Remove(origin);
            options.Log?.Invoke("Microphone blocked by Windows privacy settings (" + privacy + ").");
            return false;
        }
        bool confirmed;
        try
        {
            confirmed = options.Confirm is null || await options.Confirm(origin, source).ConfigureAwait(true);
        }
        catch (Exception)
        {
            confirmed = false;
        }
        if (!confirmed)
        {
            armed.Remove(origin);
            options.Log?.Invoke("Microphone request declined by the host.");
            return false;
        }
        armed[origin] = Now + GrantWindow;
        options.Log?.Invoke("Microphone request granted for " + origin + " (" + source + ").");
        return true;
    }

    /// <summary>The WebView2 media decision for a permission request from <paramref name="requestingUri"/>.</summary>
    public MediaDecision Decide(MediaKind kind, string? requestingUri)
    {
        if (kind == MediaKind.Other) return MediaDecision.Default;
        if (kind == MediaKind.Camera) return MediaDecision.Deny;
        var origin = BusymateOrigins.FromUri(requestingUri);
        if (origin is null || !Origins.Contains(origin)) return MediaDecision.Deny;
        if (!armed.TryGetValue(origin, out var until) || Now > until) return MediaDecision.Deny;
        if (!ReadPrivacy().Allows())
        {
            armed.Remove(origin);
            return MediaDecision.Deny;
        }
        return MediaDecision.Allow;
    }

    // ── WebView2 installation ───────────────────────────────────────────────

    /// <summary>Installs the adapter BEFORE the first navigation. Null when no valid origin is allowed.</summary>
    public static async Task<Installation?> InstallAsync(CoreWebView2 core, Options options)
    {
        if (core is null) throw new ArgumentNullException(nameof(core));
        var microphone = new BusymateMicrophone(options);
        if (microphone.Origins.Count == 0) return null;
        var installation = new Installation(core, microphone);
        await BusymateWebMessageHub.For(core).RegisterAsync(Name, microphone.Origins, installation.ReceiveAsync)
            .ConfigureAwait(true);
        core.PermissionRequested += installation.OnPermissionRequested;
        return installation;
    }

    /// <summary>Maps a WebView2 permission kind.</summary>
    public static MediaKind KindOf(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => MediaKind.Microphone,
        CoreWebView2PermissionKind.Camera => MediaKind.Camera,
        _ => MediaKind.Other,
    };

    /// <summary>One adapter installed on one CoreWebView2.</summary>
    public sealed class Installation
    {
        private readonly CoreWebView2 core;

        internal Installation(CoreWebView2 core, BusymateMicrophone microphone)
        {
            this.core = core;
            Microphone = microphone;
        }

        public BusymateMicrophone Microphone { get; }

        /// <summary>Stops granting; the page object stays inert until the WebView is closed.</summary>
        public void Uninstall()
        {
            try { core.PermissionRequested -= OnPermissionRequested; } catch (Exception) { /* closed */ }
        }

        internal async Task ReceiveAsync(BusymateHubMessage message)
        {
            var reply = await Microphone.HandleAsync(message.Data, message.Source).ConfigureAwait(true);
            if (reply is not null) message.Target.Post(Name, reply, message.Origin);
        }

        internal void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            try
            {
                var decision = Microphone.Decide(KindOf(e.PermissionKind), e.Uri);
                if (decision == MediaDecision.Default) return;
                e.State = decision == MediaDecision.Allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                // Never persist: every session re-asks through the chat's tap.
                e.SavesInProfile = false;
            }
            catch (Exception)
            {
                try { e.State = CoreWebView2PermissionState.Deny; } catch (Exception) { /* gone */ }
            }
        }
    }
}

/// <summary>Windows microphone privacy state (Settings › Privacy &amp; security › Microphone).</summary>
public enum MicrophonePrivacy
{
    /// <summary>Explicitly allowed.</summary>
    Allowed,
    /// <summary>Not recorded (treated as allowed; Windows itself still enforces).</summary>
    Unknown,
    /// <summary>"Microphone access" is off for this device.</summary>
    DeniedForDevice,
    /// <summary>"Let apps access your microphone" is off.</summary>
    DeniedForApps,
    /// <summary>"Let desktop apps access your microphone" is off.</summary>
    DeniedForDesktopApps,
}

/// <summary>Reads the Windows microphone privacy switches from the CapabilityAccessManager consent store.</summary>
public static class WindowsMicrophonePrivacy
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>True unless Windows privacy settings deny microphone access to this desktop app.</summary>
    public static bool Allows(this MicrophonePrivacy privacy) =>
        privacy is MicrophonePrivacy.Allowed or MicrophonePrivacy.Unknown;

    /// <summary>Reads the current state. Packaged (MSIX) apps are additionally governed by their own per-app switch.</summary>
    public static MicrophonePrivacy Read()
    {
        try
        {
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            var device = ValueOf(machine, Key);
            var apps = ValueOf(user, Key);
            var desktop = ValueOf(user, Key + @"\NonPackaged");
            if (IsDeny(device)) return MicrophonePrivacy.DeniedForDevice;
            if (IsDeny(apps)) return MicrophonePrivacy.DeniedForApps;
            if (IsDeny(desktop)) return MicrophonePrivacy.DeniedForDesktopApps;
            return IsAllow(device) || IsAllow(apps) || IsAllow(desktop) ? MicrophonePrivacy.Allowed : MicrophonePrivacy.Unknown;
        }
        catch (Exception)
        {
            return MicrophonePrivacy.Unknown;
        }
    }

    /// <summary>Opens Windows microphone privacy settings. Call only from an explicit user action; the adapter never does.</summary>
    public static void OpenSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true })?.Dispose();
    }

    private static string? ValueOf(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue("Value") as string;
    }

    private static bool IsDeny(string? value) => string.Equals(value, "Deny", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllow(string? value) => string.Equals(value, "Allow", StringComparison.OrdinalIgnoreCase);
}
