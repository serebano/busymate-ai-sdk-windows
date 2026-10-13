# Native microphone integration for Windows — adapter 1.0.0

This guide adds microphone permission handling to a hosted chat in your Windows app. A user taps the chat microphone or voice button; the chat sends an event to the app; the app checks Windows microphone privacy (and, optionally, asks with your own dialog); and the chat starts capture only after the app says it is granted. No separate native permission button is needed. Opening chat never asks.

## How Windows differs from iOS and Android

Unpackaged Windows desktop apps get no per-app runtime microphone prompt from the OS. Access is governed by **Settings › Privacy & security › Microphone**: "Microphone access" (device), "Let apps access your microphone" and "Let desktop apps access your microphone". WebView2 adds its own permission decision for `getUserMedia` through `CoreWebView2.PermissionRequested`. The adapter covers both:

1. **The tap event.** The chat posts `{"type":"busymate.microphone.v1.request","id":…,"source":"dictation"|"voice"}` on `window.BusymateMicrophone`. The adapter accepts it only from an allowed origin (as reported by WebView2, never by the page), re-reads the three Windows privacy switches, optionally awaits your `Confirm` callback, and replies `{"id":…,"granted":true|false}`.
2. **The WebView2 decision.** `PermissionRequested` is allowed **only** for `Microphone`, **only** for an allowed requesting origin, **only** within 60 seconds of a tap-driven grant for that origin, and **only** while Windows privacy still allows it. Camera is denied. Grants are never saved in the WebView2 profile. Other permission kinds (notifications, clipboard…) are left to your app.

Packaged (MSIX) apps additionally have their own per-app switch, which Windows enforces at capture time; the adapter reads the device, apps and desktop-apps switches.

## Install

```csharp
var microphone = await BusymateMicrophone.InstallAsync(webView.CoreWebView2, new BusymateMicrophone.Options
{
    Origins = new[] { "https://YOUR_ASSISTANT.busymate.ai" },
    // Optional: your own consent dialog after the tap.
    Confirm = (origin, source) => Task.FromResult(AskUser(origin, source)),
});
// Configure your identity bridge too, then:
webView.CoreWebView2.Navigate("https://YOUR_ASSISTANT.busymate.ai");
```

Or pass `Microphone = new BusymateMicrophone.Options { … }` to `BusymateChatOptions` and use `BusymateChatView` (WPF) / `BusymateChatPanel` (WinForms). Install **before the first navigation**, once per `CoreWebView2`. With `Microphone = null`, `BusymateChat` denies media requests rather than showing WebView2's own prompt.

Allow only the exact HTTPS origins serving your assistant: scheme + host + optional port, no trailing path, credentials, wildcard, query or fragment. Host names normalize to lowercase; port 443 equals no port. Navigating the WebView to an untrusted origin never grants access. For a chat iframe inside your own page, that page must also allow the microphone through its iframe Permissions Policy.

This is native app code: integrating it requires **your app's own release**.

## Denial, Retry, revoked, cancel

- **Denied by Windows privacy:** the reply is `granted: false` and the chat shows its blocked notice. The adapter never opens Settings itself. Offer your own button that calls `WindowsMicrophonePrivacy.OpenSettings()` (`ms-settings:privacy-microphone`).
- **Retry:** **Try again** in the chat sends a new request; the adapter re-reads privacy and asks `Confirm` again. Concurrent requests share one pending ask.
- **Revoked:** every request and every WebView2 permission decision re-reads Windows privacy, so a switch turned off in Settings blocks the next activation. Windows also blocks capture itself.
- **Cancel:** cancel dictation with another microphone tap, or leave voice mode with its keyboard control. The chat ignores a late reply after cancel or unmount; the 60-second grant window lets nothing start without the chat asking for it. Cancelling chat does not dismiss your own `Confirm` dialog.

## Event and reply contract

The page object is `BusymateMicrophone`. The request is a **JSON string**:

```json
{"type":"busymate.microphone.v1.request","id":"mic-unique-id","source":"dictation"}
```

The reply is a JSON string delivered as a `message` event on the same object:

```json
{"id":"mic-unique-id","granted":true}
```

Messages are bounded to 4096 characters. Do not invoke the bridge from a page-load script or request permission when opening chat: the chat owns the user-tap trigger and request correlation. A native refusal, invalid reply or 60-second timeout shows the blocked notice.

## Troubleshooting

| Symptom | Check |
|---|---|
| Tap shows the blocked notice at once | Windows privacy denies the microphone (see the log / `WindowsMicrophonePrivacy.Read()`), your `Confirm` returned false, or no microphone is present. |
| Granted, chat still cannot record | The requesting origin must be in `Origins`; `getUserMedia` must follow the tap within 60 seconds; check iframe Permissions Policy and the WebView2 runtime. |
| No request reaches the app | Install before the first `Navigate`; the page's own origin must be allowed, or `window.BusymateMicrophone` is not defined. |
| Camera request denied | Expected: this adapter provides microphone access only. |

## Acceptance checklist for your app release

Build the integration in your own project, then verify on supported physical Windows 10 and 11 machines with a microphone:

1. Fresh state: open chat with no prompt; tap **microphone**, (accept your own dialog if configured), and dictate successfully. Repeat separately for **voice mode**.
2. Turn off "Let desktop apps access your microphone": the next tap shows the blocked notice with no capture or prompt loop. Turn it back on and use **Try again** successfully.
3. Already granted: another activation starts without an unnecessary dialog. Revoke in Settings during a session and confirm the next activation is blocked.
4. While your consent dialog is open, cancel dictation or leave voice mode; accept afterwards and confirm capture does not start from the cancelled action.
5. Kill the `msedgewebview2.exe` renderer and then the browser process (Task Manager): the chat reloads and the identity bridge sends `restored`; repeat an activation.
6. Confirm untrusted origins and camera requests are refused, and test each WebView2 runtime channel you support.

The SDK's CI compiles the library and example and unit-tests the request parsing, origin checks and grant decisions. It does not drive a live WebView2, Windows privacy switches or a microphone. Record the checklist results with your own app release before rollout.

## Platform references

- [WebView2: PermissionRequested](https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2.permissionrequested)
- [WebView2: web messaging](https://learn.microsoft.com/microsoft-edge/webview2/how-to/communicate-btwn-web-native)
- [WebView2: process failures](https://learn.microsoft.com/microsoft-edge/webview2/concepts/process-related-events)
- [Windows: microphone privacy settings](https://support.microsoft.com/windows/turn-on-app-permissions-for-your-microphone-in-windows-94991183-f69d-b4cf-4679-c98ca45f577a)
