# Configuration reference

## Identity bridge (`BusymateBridge.Config`)

| Setting | Behavior |
|---|---|
| `Assistant` | Required assistant slug; adds exactly `https://<assistant>.busymate.ai` to allowed origins. Obtain the actual assistant URL from Busymate; a marketing page is not a chat URL. |
| `Origins` | Extra exact HTTPS origins for mapped support hosts or the `busymate.ai` support route. No wildcards; invalid entries are ignored. |
| `Account` | Delegate returning the current account ID or null, read when state/mint is requested. |
| `Mint` | Required authenticated-backend callback `(request, CancellationToken) → Task<MintResult>`, a fresh token/nonce for every request; never a cached assertion. |
| `OnAction` | Optional fixed app-action handler; return true only when handled. |
| `OnClose` | Optional host dismissal callback; fallback for the `close` action. |
| `OpenUrl` | Optional `(Uri, mode) → bool` for the `open` operation; default opens the system browser. https only. |
| `AppId` | Optional stable app id for the account hash; default is the entry assembly name. |

`BusymateBridge.AllowedOrigins(config)` returns the normalized allow-list. `hello` reports `platform: "windows"`, `transport: "message"`, the WebView2 runtime version as `webview`, and the entry assembly's name and version as `app`.

## Microphone adapter (`BusymateMicrophone.Options`)

| Setting | Behavior |
|---|---|
| `Origins` | Exact HTTPS origins serving the chat, independent of the identity list. Use `BusymateOrigins.ForAssistant(assistant, origins)` to reuse it. |
| `Confirm` | Optional `(origin, source) → Task<bool>` native consent shown after the user's tap. Default: the tap itself is consent. |
| `Privacy` | Optional privacy reader for tests; default reads the Windows consent store. |
| `Log` | Optional diagnostics. |

There is no setting for automatic prompts on page load, camera access, opening Settings automatically, timeout override, audio-device selection, provider credentials or token caching. Request type `busymate.microphone.v1.request`, source `dictation` or `voice`, correlated `id`; the reply has the same `id` and a boolean `granted`. See [microphone integration](microphone.md).

## Host (`BusymateChatOptions`)

| Setting | Behavior |
|---|---|
| `Identity` | Identity bridge config; null keeps the chat a guest (no bridge object on the page). |
| `Microphone` | Microphone adapter options; null denies WebView2 microphone and camera requests instead of falling back to the WebView2 prompt. |
| `RestrictNavigation` | Default true: top-level navigation stays on the chat URL's origin and the identity, microphone and navigation origins. Trusting a navigation origin never adds it to a bridge allow-list. |
| `NavigationOrigins` | Extra exact HTTPS origins for top-level navigation. |
| `OpenExternal` | Opens untrusted user-clicked https links; default is the system browser. |
| `UserDataFolder` | WebView2 user data folder; default `%LOCALAPPDATA%\Busymate.AI\<app>\WebView2`. |
| `Log` | Diagnostics without tokens, nonces, raw account IDs or audio. |

Hosted chat appearance, theme, locale, voice availability and tenant behavior are configured through the hosted chat/embed surface and Busymate Console, not by native SDK flags. Preserve the tenant-generated chat URL, including its supported query parameters; use `channel=desktop_sdk` for a Windows app (never `desktop`, which is Busymate's own Console app, or `embed`, which requires an allow-listed parent web page). The SDK does not own window chrome, layout or audio routing; your host does.

## Windows-specific behavior

**Transport.** WebView2 has one page channel. `BusymateWebMessageHub` injects (with `AddScriptToExecuteOnDocumentCreatedAsync`) a `window.BusymateBridge` / `window.BusymateMicrophone` object with `postMessage(string)` and `message` events, the shape the hosted chat already uses on Android. The object is defined only in documents whose own origin is allowed, and the app re-checks the sender from WebView2's `Source` on every message. Replies go back with `PostWebMessageAsJson` only while the target document still has the requesting origin. Frames are supported where the runtime exposes `CoreWebView2Frame` messaging; the top-level chat page is the supported shape.

**Process failures.** `CoreWebView2.ProcessFailed` is handled: a renderer that exits or hangs is reloaded at the last trusted URL and the bridge sends `restored` after the next `hello`; a browser-process exit raises `BusymateChat.BrowserProcessExited`, and the helper controls recreate the WebView2 (at most three times) and mark the restore. Frame-renderer and helper-process exits are logged; WebView2 restarts those itself.

**Foreground.** The helper controls send `resume` when their window is activated again after being deactivated. With your own host, call `BusymateChat.NotifyResume()`.

**Disposal.** `BusymateChat.Detach()` removes host handlers; dispose the WebView2 control with its owner. Install once per `CoreWebView2`; never install again on each tap or on re-render.
