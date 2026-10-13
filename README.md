# Busymate AI SDK for Windows — 1.0.1

Official independent Windows library for Busymate AI hosted chat in Microsoft Edge WebView2. `Busymate.AI` is a .NET 8 (`net8.0-windows`) library with `BusymateBridge` (identity wire v2, immutable build 2.0.0), `BusymateMicrophone` (wire 1, adapter 1.0.0), exact origin allowlisting, WebView2 process-failure recovery, and WPF / WinForms helper controls. Windows has no legacy v1 identity bridge; use v2.

## Install

The versioned package is the `Busymate.AI.1.0.1.nupkg` attached to the [1.0.1 GitHub release](https://github.com/serebano/busymate-ai-sdk-windows/releases/tag/1.0.1). It is not published to nuget.org. Use it from a local package source:

```sh
mkdir packages
# download Busymate.AI.1.0.1.nupkg from the release into ./packages
dotnet nuget add source ./packages --name busymate-local
dotnet add package Busymate.AI --version 1.0.1
```

You can also reference the source: clone this repository at the **1.0.1** tag and add a `ProjectReference` to `src/Busymate.AI/Busymate.AI.csproj`.

Requirements: Windows 10/11 desktop app targeting `net8.0-windows`; the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) installed (Windows 11 ships it); package dependency `Microsoft.Web.WebView2` 1.0.2903.40 or a compatible newer version.

## Integrate

The quickest path is the WPF helper control (`Busymate.AI.Wpf.BusymateChatView`) or the WinForms one (`Busymate.AI.WinForms.BusymateChatPanel`):

```csharp
using Busymate.AI;

await ChatView.StartAsync(
    new Uri("https://busymate.ai/support/acme?channel=desktop_sdk&locale=en"),
    new BusymateChatOptions
    {
        // null = guest chat, no identity bridge
        Identity = new BusymateBridge.Config
        {
            Assistant = "acme",
            Origins = new[] { "https://busymate.ai", "https://support.acme.com" },
            Account = () => session.UserIdOrNull,
            Mint = (request, ct) => backend.MintBusymateAsync(request, ct), // YOUR authenticated backend
            OnClose = () => HideChat(),
        },
        Microphone = new BusymateMicrophone.Options
        {
            Origins = BusymateOrigins.ForAssistant("acme", new[] { "https://busymate.ai", "https://support.acme.com" }),
        },
    });
```

The control creates the WebView2 with a per-user data folder, installs both bridges **before the first navigation**, keeps top-level navigation on trusted origins, denies camera, recovers from renderer and browser process failures, and reports window re-activation as `resume`. On an existing `CoreWebView2`, call `BusymateChat.AttachAsync(core, options)` before `Navigate`, or install the pieces yourself with `BusymateBridge.InstallAsync` and `BusymateMicrophone.InstallAsync`.

1. Read [identity and authentication](docs/identity.md).
2. Configure the [complete SDK reference](docs/configuration.md).
3. Follow [microphone and voice integration](docs/microphone.md).
4. Validate on real Windows machines using the acceptance checklist.

The default guest route is `https://busymate.ai/support/busyproxy?channel=desktop_sdk&locale=en`. `desktop_sdk` is the hosted product's channel for third-party desktop apps built on the official desktop SDKs: the chat loads as the window's top-level page with no parent frame, uses the normal chat layout (no Busymate Console title-bar inset), applies `locale` on the first frame, and Inbox sessions are labelled "Desktop app (SDK)". Do not use `desktop`, which is reserved for Busymate's own Console app, or `embed`, which requires an allow-listed parent web page and refuses a top-level window. Human integration guide: [busymate.ai/docs/guides/windows-sdk](https://busymate.ai/docs/guides/windows-sdk).

## Run the example

[`example-app/Busymate.AI.Example.Wpf`](example-app/README.md) is a WPF app with a settings panel (chat URL, locale, origins, guest vs identity mint callback, microphone on/off, native consent dialog, close/ready handlers, navigation restriction) and a callbacks log. On Windows with the .NET 8 SDK:

```sh
dotnet run --project example-app/Busymate.AI.Example.Wpf
```

## Build and versions

On Windows with the .NET 8 SDK: `python scripts/verify-release.py`, then `dotnet build src/Busymate.AI/Busymate.AI.csproj -c Release`, `dotnet build example-app/Busymate.AI.Example.Wpf/Busymate.AI.Example.Wpf.csproj -c Release`, `dotnet test tests/Busymate.AI.Tests/Busymate.AI.Tests.csproj -c Release` and `dotnet pack src/Busymate.AI/Busymate.AI.csproj -c Release -o artifacts`. The `sdk-check` CI job runs exactly these on `windows-latest` and uploads the `.nupkg` and the published example. CI compiles and unit-tests the bridge logic; it does not drive a live WebView2, the Windows microphone privacy switches or a microphone. Those need a real Windows machine (see the checklist in [docs/microphone.md](docs/microphone.md)).

Distribution 1.0.1 differs from identity wire 2/build 2.0.0 and microphone wire 1/adapter 1.0.0. The [release manifest](release-manifest.json) pins the source hashes of the wire files and the contract. This repo owns Windows SDK releases. See the [changelog](CHANGELOG.md) and the [v2 wire contract](CONTRACT-v2.md).
