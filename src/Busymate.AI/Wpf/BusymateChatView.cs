// Busymate AI — WPF helper control hosting the chat in WebView2.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace Busymate.AI.Wpf;

/// <summary>
/// A WPF control that creates a WebView2, attaches <see cref="BusymateChat"/> before the
/// first navigation, recreates itself when the WebView2 browser process exits, and
/// reports window re-activation as `resume`.
/// </summary>
public sealed class BusymateChatView : ContentControl, IDisposable
{
    private const int MaxRecoveries = 3;
    private WpfWebView2? webView;
    private Uri? chatUrl;
    private BusymateChatOptions? options;
    private Window? window;
    private bool deactivated;
    private int recoveries;
    private int generation;

    public BusymateChatView()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The attached chat, once started.</summary>
    public BusymateChat? Chat { get; private set; }

    /// <summary>The hosted WebView2 control, once started.</summary>
    public WpfWebView2? WebView => webView;

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

    /// <summary>Disposes the WebView2.</summary>
    public void Dispose()
    {
        generation++;
        TearDown();
    }

    private async Task CreateAsync(bool restored)
    {
        var current = ++generation;
        TearDown();
        var view = new WpfWebView2();
        webView = view;
        Content = view;
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
        // Recreate after the failed event returns; a fresh environment starts a new browser process.
        Dispatcher.BeginInvoke(new Action(async () =>
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
            Content = null;
            try { old.Dispose(); } catch (Exception) { /* already gone */ }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (ReferenceEquals(owner, window)) return;
        Detach(window);
        window = owner;
        if (window is null) return;
        window.Activated += OnActivated;
        window.Deactivated += OnDeactivated;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Detach(window);
        window = null;
    }

    private void Detach(Window? owner)
    {
        if (owner is null) return;
        owner.Activated -= OnActivated;
        owner.Deactivated -= OnDeactivated;
    }

    private void OnDeactivated(object? sender, EventArgs e) => deactivated = true;

    private void OnActivated(object? sender, EventArgs e)
    {
        if (!deactivated) return;
        deactivated = false;
        Chat?.NotifyResume();
    }
}
