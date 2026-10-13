// Busymate AI — WinForms helper control hosting the chat in WebView2.

using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using FormsWebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Busymate.AI.WinForms;

/// <summary>
/// A WinForms control that creates a WebView2, attaches <see cref="BusymateChat"/> before the
/// first navigation, recreates itself when the WebView2 browser process exits, and
/// reports form re-activation as `resume`.
/// </summary>
public sealed class BusymateChatPanel : UserControl
{
    private const int MaxRecoveries = 3;
    private FormsWebView2? webView;
    private Uri? chatUrl;
    private BusymateChatOptions? options;
    private Form? form;
    private bool deactivated;
    private int recoveries;
    private int generation;

    /// <summary>The attached chat, once started.</summary>
    public BusymateChat? Chat { get; private set; }

    /// <summary>The hosted WebView2 control, once started.</summary>
    public FormsWebView2? WebView => webView;

    /// <summary>Raised when the WebView2 runtime cannot start (for example, it is not installed).</summary>
    public event EventHandler<Exception>? StartFailed;

    /// <summary>Creates the WebView2, attaches the bridges and loads <paramref name="url"/>.</summary>
    public Task StartAsync(Uri url, BusymateChatOptions chatOptions)
    {
        chatUrl = url ?? throw new ArgumentNullException(nameof(url));
        options = chatOptions ?? throw new ArgumentNullException(nameof(chatOptions));
        recoveries = 0;
        return CreateAsync(restored: false);
    }

    /// <summary>Reloads the chat.</summary>
    public void Reload()
    {
        try { webView?.CoreWebView2?.Reload(); } catch (Exception) { /* not started */ }
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        var owner = FindForm();
        if (ReferenceEquals(owner, form)) return;
        if (form is not null)
        {
            form.Activated -= OnActivated;
            form.Deactivate -= OnDeactivate;
        }
        form = owner;
        if (form is null) return;
        form.Activated += OnActivated;
        form.Deactivate += OnDeactivate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            generation++;
            TearDown();
            if (form is not null)
            {
                form.Activated -= OnActivated;
                form.Deactivate -= OnDeactivate;
                form = null;
            }
        }
        base.Dispose(disposing);
    }

    private async Task CreateAsync(bool restored)
    {
        var current = ++generation;
        TearDown();
        var view = new FormsWebView2 { Dock = DockStyle.Fill };
        webView = view;
        Controls.Add(view);
        try
        {
            var environment = await BusymateChat.CreateEnvironmentAsync(options!.UserDataFolder).ConfigureAwait(true);
            await view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            if (current != generation) return;
            var chat = await BusymateChat.AttachAsync(view.CoreWebView2, options).ConfigureAwait(true);
            if (current != generation)
            {
                chat.Detach();
                return;
            }
            Chat = chat;
            chat.BrowserProcessExited += OnBrowserProcessExited;
            if (restored) chat.MarkRestorePending();
            chat.Navigate(chatUrl!);
        }
        catch (Exception error)
        {
            if (current != generation) return;
            options?.Log?.Invoke("WebView2 could not start: " + error.GetType().Name + ".");
            StartFailed?.Invoke(this, error);
        }
    }

    private void OnBrowserProcessExited(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (recoveries >= MaxRecoveries)
        {
            options?.Log?.Invoke("WebView2 failed repeatedly; not recreating it again.");
            TearDown();
            return;
        }
        recoveries++;
        BeginInvoke(new Action(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(true);
            await CreateAsync(restored: true).ConfigureAwait(true);
        }));
    }

    private void TearDown()
    {
        if (Chat is not null)
        {
            Chat.BrowserProcessExited -= OnBrowserProcessExited;
            Chat.Detach();
            Chat = null;
        }
        if (webView is not null)
        {
            var old = webView;
            webView = null;
            Controls.Remove(old);
            try { old.Dispose(); } catch (Exception) { /* already gone */ }
        }
    }

    private void OnDeactivate(object? sender, EventArgs e) => deactivated = true;

    private void OnActivated(object? sender, EventArgs e)
    {
        if (!deactivated) return;
        deactivated = false;
        Chat?.NotifyResume();
    }
}
