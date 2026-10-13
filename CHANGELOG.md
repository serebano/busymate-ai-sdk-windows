# Changelog

## 1.0.1

The default hosted chat URL now uses `channel=desktop_sdk` (`https://busymate.ai/support/busyproxy?channel=desktop_sdk&locale=en`), Busymate's channel for third-party desktop SDK apps (serebano/busymate-ai-src#1849): the chat loads top-level with the normal chat layout, without the Busymate Console title-bar inset, and with the locale applied on the first frame. `desktop` stays reserved for Busymate's own Console app. Documentation, the WPF example defaults and tests are updated. The identity wire (v2, build 2.0.0), the microphone adapter (1.0.0), the contract and every hashed wire file are byte-identical to 1.0.0.

## 1.0.0

First independent Windows distribution: the `Busymate.AI` .NET 8 library on WebView2 with the identity v2 bridge (wire 2, build 2.0.0), the microphone adapter (wire 1, adapter 1.0.0), exact origin allowlisting, WebView2 process-failure recovery and WPF / WinForms helper controls, plus a WPF example app. The wire messages match the iOS and Android SDKs and the hosted chat's bridge client. No legacy v1 identity bridge is provided on Windows. Hosted chat receives updates separately; your desktop app release is needed to install or upgrade native SDK code.
