using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Busymate.AI.Example;

/// <summary>In-memory demo configuration. Bearer and account values are never persisted or logged.</summary>
public sealed record DemoSettings
{
    private static readonly Regex LocalePattern = new(@"\A[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*\z", RegexOptions.CultureInvariant);

    public string Assistant { get; init; } = "busyproxy";
    public string ChatUrl { get; init; } = "https://busymate.ai/support/busyproxy?channel=desktop&locale=en";
    public string Locale { get; init; } = "en";
    public string ExtraOrigins { get; init; } = "https://busymate.ai";
    /// <summary>False: guest (no identity bridge). True: identity bridge with the backend mint callback.</summary>
    public bool IdentityEnabled { get; init; }
    public bool MicrophoneEnabled { get; init; } = true;
    public bool ConfirmMicrophone { get; init; }
    public bool CloseEnabled { get; init; } = true;
    public bool ActionsEnabled { get; init; } = true;
    public bool RestrictNavigation { get; init; } = true;
    public string BackendUrl { get; init; } = "";
    public string AccountId { get; init; } = "";
    public string BackendBearer { get; init; } = "";

    public IReadOnlyList<string> Origins() =>
        ExtraOrigins.Split(',').Select(o => o.Trim()).Where(o => o.Length > 0).ToList();

    /// <summary>The chat URL with the locale setting applied (an empty locale keeps the URL as typed).</summary>
    public Uri EffectiveChatUrl()
    {
        var builder = new UriBuilder(ChatUrl);
        if (Locale.Length == 0) return builder.Uri;
        var query = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("locale=", StringComparison.OrdinalIgnoreCase))
            .Append("locale=" + Uri.EscapeDataString(Locale));
        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    public string? ValidationError()
    {
        if (!BusymateOrigins.IsAssistantSlug(Assistant)) return "Use a valid assistant slug.";
        if (!IsHttpsUrl(ChatUrl)) return "Chat URL must use HTTPS without credentials.";
        if (Locale.Length > 0 && !LocalePattern.IsMatch(Locale)) return "Locale must look like en or pt-BR.";
        if (Origins().Any(o => !ValidOrigin(o))) return "Origins must be exact HTTPS scheme + host + optional port.";
        if (BackendUrl.Length > 0 && !IsHttpsUrl(BackendUrl)) return "Backend URL must use HTTPS.";
        if (IdentityEnabled && BackendUrl.Length == 0) return "Identity mode needs your authenticated backend mint URL.";
        return null;
    }

    public static bool ValidOrigin(string value)
    {
        if (value.Contains('*') || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)) return false;
        // Scheme + host + optional port only: no path, query, fragment or credentials.
        var rest = value.Substring("https://".Length);
        return url.Host.Length > 0 && rest.IndexOfAny(new[] { '/', '?', '#', '@' }) < 0 &&
               BusymateOrigins.Normalize(value) is not null;
    }

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps &&
        url.Host.Length > 0 && url.UserInfo.Length == 0;

    /// <summary>The identity bridge configuration, or null in guest mode.</summary>
    public BusymateBridge.Config? IdentityConfig(
        Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<BusymateBridge.MintResult>> mint,
        Action<string> log,
        Action close)
    {
        if (!IdentityEnabled) return null;
        var account = AccountId;
        return new BusymateBridge.Config
        {
            Assistant = Assistant,
            Origins = Origins(),
            Account = () => DemoSession.SignedIn && account.Length > 0 ? account : null,
            Mint = mint,
            OnAction = ActionsEnabled ? (name, _) => { log("Native action: " + name); return name == "ready"; } : null,
            OnClose = CloseEnabled ? () => { log("Chat requested host close."); close(); } : null,
        };
    }

    /// <summary>The complete chat options for the helper control.</summary>
    public BusymateChatOptions ChatOptions(
        Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<BusymateBridge.MintResult>> mint,
        Action<string> log,
        Action close,
        Func<string, string, Task<bool>>? confirmMicrophone)
    {
        return new BusymateChatOptions
        {
            Identity = IdentityConfig(mint, log, close),
            Microphone = MicrophoneEnabled
                ? new BusymateMicrophone.Options
                {
                    Origins = BusymateOrigins.ForAssistant(Assistant, Origins()),
                    Confirm = ConfirmMicrophone ? confirmMicrophone : null,
                    Log = log,
                }
                : null,
            RestrictNavigation = RestrictNavigation,
            NavigationOrigins = BusymateOrigins.ForAssistant(Assistant, Origins()),
            Log = log,
        };
    }
}

/// <summary>Process-memory demo session.</summary>
public static class DemoSession
{
    public static DemoSettings Settings { get; set; } = new();
    public static bool SignedIn { get; set; }
}
