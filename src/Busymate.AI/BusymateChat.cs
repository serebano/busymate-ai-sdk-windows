// Busymate AI — host orchestration for a hosted chat in WebView2.
//
// Not part of the frozen wire: this file wires the identity bridge and the
// microphone adapter to one CoreWebView2 and adds the host policy around them —
// process-failure recovery, top-level navigation restricted to trusted origins,
// media denial when the microphone adapter is off, and resume / account events.
// The WPF and WinForms helper controls use it; you may use it directly.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Busymate.AI;

/// <summary>Options for <see cref="BusymateChat"/> and the helper controls.</summary>
public sealed class BusymateChatOptions
{
    /// <summary>The identity bridge; null keeps the chat a guest (no identity bridge installed).</summary>
    public BusymateBridge.Config? Identity { get; init; }

    /// <summary>The microphone adapter; null denies WebView2 microphone and camera requests.</summary>
    public BusymateMicrophone.Options? Microphone { get; init; }

    /// <summary>Keep top-level navigation on trusted https origins (default true).</summary>
    public bool RestrictNavigation { get; init; } = true;

    /// <summary>Extra exact https origins the top-level document may navigate to.</summary>
    public IReadOnlyList<string> NavigationOrigins { get; init; } = Array.Empty<string>();

    /// <summary>Opens an untrusted https link the user clicked; default is the system browser. Return false to ignore.</summary>
    public Func<Uri, bool>? OpenExternal { get; init; }

    /// <summary>WebView2 user data folder; default %LOCALAPPDATA%\Busymate.AI\&lt;app&gt;\WebView2.</summary>
    public string? UserDataFolder { get; init; }

    /// <summary>Diagnostics. Never receives tokens, nonces, raw account ids or audio.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>A hosted Busymate chat attached to one CoreWebView2.</summary>
public sealed class BusymateChat
{
    private readonly CoreWebView2 core;
    private readonly BusymateChatOptions options;
    private string? lastTrustedUrl;
    private bool detached;

    private BusymateChat(CoreWebView2 core, BusymateChatOptions options)
    {
        this.core = core;
        this.options = options;
    }

    public BusymateBridge.Installation? Identity { get; private set; }

    public BusymateMicrophone.Installation? Microphone { get; private set; }

    /// <summary>Trusted top-level origins: identity + microphone + navigation origins.</summary>
    public IReadOnlyList<string> TrustedOrigins { get; private set; } = Array.Empty<string>();

    /// <summary>The WebView2 browser process exited: this CoreWebView2 is unusable; recreate the WebView2 control.</summary>
    public event EventHandler<CoreWebView2ProcessFailedEventArgs>? BrowserProcessExited;

    /// <summary>Creates a WebView2 environment with a per-user data folder.</summary>
    public static Task<CoreWebView2Environment> CreateEnvironmentAsync(string? userDataFolder = null) =>
        CoreWebView2Environment.CreateAsync(null, userDataFolder ?? DefaultUserDataFolder());

    /// <summary>%LOCALAPPDATA%\Busymate.AI\&lt;entry assembly&gt;\WebView2</summary>
    public static string DefaultUserDataFolder()
    {
        var app = Assembly.GetEntryAssembly()?.GetName().Name ?? "app";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Busymate.AI", app, "WebView2");
    }

    /// <summary>Attaches to a CoreWebView2 BEFORE its first navigation.</summary>
    public static async Task<BusymateChat> AttachAsync(CoreWebView2 core, BusymateChatOptions options)
    {
        if (core is null) throw new ArgumentNullException(nameof(core));
        if (options is null) throw new ArgumentNullException(nameof(options));
        var chat = new BusymateChat(core, options);
        if (options.Identity is not null) chat.Identity = await BusymateBridge.InstallAsync(core, options.Identity).ConfigureAwait(true);
        if (options.Microphone is not null) chat.Microphone = await BusymateMicrophone.InstallAsync(core, options.Microphone).ConfigureAwait(true);
        chat.TrustedOrigins = TrustedOriginsOf(options);
        core.PermissionRequested += chat.OnPermissionRequested;
        core.ProcessFailed += chat.OnProcessFailed;
        core.NavigationStarting += chat.OnNavigationStarting;
        core.NewWindowRequested += chat.OnNewWindowRequested;
        core.NavigationCompleted += chat.OnNavigationCompleted;
        chat.Log("Chat attached: identity " + (chat.Identity is null ? "off (guest)" : "on") +
                 ", microphone " + (chat.Microphone is null ? "off" : "on") + ".");
        return chat;
    }

    /// <summary>The identity + microphone + navigation allow-list, normalized.</summary>
    public static IReadOnlyList<string> TrustedOriginsOf(BusymateChatOptions options)
    {
        var all = new List<string>();
        if (options.Identity is not null) all.AddRange(BusymateBridge.AllowedOrigins(options.Identity));
        if (options.Microphone is not null) all.AddRange(BusymateOrigins.Exact(options.Microphone.Origins));
        all.AddRange(BusymateOrigins.Exact(options.NavigationOrigins));
        return all.Distinct(StringComparer.Ordinal).ToList().AsReadOnly();
    }

    /// <summary>True when a top-level navigation to <paramref name="uri"/> stays in the chat.</summary>
    public static bool IsTrustedNavigation(string? uri, IReadOnlyList<string> trusted)
    {
        if (string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        var origin = BusymateOrigins.FromUri(uri);
        return origin is not null && trusted.Contains(origin);
    }

    /// <summary>Navigates to the chat URL (https only).</summary>
    public void Navigate(Uri chatUrl)
    {
        if (chatUrl is null || chatUrl.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("The chat URL must use https.", nameof(chatUrl));
        lastTrustedUrl = chatUrl.AbsoluteUri;
        core.Navigate(chatUrl.AbsoluteUri);
    }

    /// <summary>Optional: the signed-in account changed (sign-in, sign-out, switch).</summary>
    public void AccountChanged() => Identity?.AccountChanged();

    /// <summary>The host window returned to the foreground.</summary>
    public void NotifyResume() => Identity?.Resumed();

    /// <summary>This chat replaces one whose process died: `restored` follows the next hello.</summary>
    public void MarkRestorePending() => Identity?.MarkRestorePending();

    /// <summary>Removes the host policy handlers (the WebView is being disposed).</summary>
    public void Detach()
    {
        if (detached) return;
        detached = true;
        try
        {
            core.PermissionRequested -= OnPermissionRequested;
            core.ProcessFailed -= OnProcessFailed;
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.NavigationCompleted -= OnNavigationCompleted;
        }
        catch (Exception)
        {
            // Already closed.
        }
        Microphone?.Uninstall();
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (Microphone is not null) return; // the adapter decides
        var kind = BusymateMicrophone.KindOf(e.PermissionKind);
        if (kind == BusymateMicrophone.MediaKind.Other) return;
        // No adapter: no silent alternative permission flow.
        e.State = CoreWebView2PermissionState.Deny;
        e.SavesInProfile = false;
        Log("Media request denied: the microphone adapter is off.");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                Log("WebView2 browser process exited; the host recreates the WebView.");
                BrowserProcessExited?.Invoke(this, e);
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                Log("Chat renderer " + (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ? "exited" : "is unresponsive") + "; reloading the chat.");
                MarkRestorePending();
                try
                {
                    if (lastTrustedUrl is not null) core.Navigate(lastTrustedUrl);
                    else core.Reload();
                }
                catch (Exception)
                {
                    // The host's BrowserProcessExited path recovers a dead browser.
                }
                break;
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                Log("A frame renderer exited; WebView2 shows its own error page in that frame.");
                break;
            default:
                Log("WebView2 helper process exited (" + e.ProcessFailedKind + "); WebView2 restarts it.");
                break;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!options.RestrictNavigation || IsTrustedNavigation(e.Uri, TrustedOrigins)) return;
        e.Cancel = true;
        Log("Blocked top-level navigation to an untrusted origin.");
        if (e.IsUserInitiated) OpenExternal(e.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // The chat never gets a second privileged window.
        e.Handled = true;
        if (e.IsUserInitiated) OpenExternal(e.Uri);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            var source = core.Source;
            if (e.IsSuccess && BusymateOrigins.FromUri(source) is { } origin && TrustedOrigins.Contains(origin)) lastTrustedUrl = source;
        }
        catch (Exception)
        {
            // Closed.
        }
    }

    private void OpenExternal(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            if (options.OpenExternal is not null) options.OpenExternal(parsed);
            else Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            Log("Opened a link in the system browser.");
        }
        catch (Exception)
        {
            Log("Could not open the link externally.");
        }
    }

    private void Log(string message)
    {
        try { options.Log?.Invoke(message); } catch (Exception) { /* diagnostics only */ }
    }
}
