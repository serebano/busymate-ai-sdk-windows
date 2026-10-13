// Busymate AI — exact origin rules shared by BusymateBridge and BusymateMicrophone.
//
// An origin is "https://host[:port]": lower-case, no trailing slash, the default
// port 443 dropped. No wildcards, paths, queries, credentials or http.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Busymate.AI;

/// <summary>Exact HTTPS origin allowlisting, identical to the iOS and Android SDKs.</summary>
public static class BusymateOrigins
{
    private static readonly Regex AssistantSlug =
        new(@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant);

    private static readonly Regex ExactOrigin =
        new(@"\Ahttps://[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?(?::[0-9]{1,5})?\z", RegexOptions.CultureInvariant);

    /// <summary>True for a valid assistant slug such as <c>acme</c>.</summary>
    public static bool IsAssistantSlug(string? value) => value is not null && AssistantSlug.IsMatch(value);

    /// <summary>
    /// Normalizes a configured origin ("https://Host:443/" becomes "https://host").
    /// Returns null for anything that is not an exact https origin.
    /// </summary>
    public static string? Normalize(string? origin)
    {
        if (origin is null) return null;
        var value = origin.Trim().ToLowerInvariant();
        if (value.EndsWith('/')) value = value[..^1];
        if (value.EndsWith(":443", StringComparison.Ordinal)) value = value[..^4];
        return ExactOrigin.IsMatch(value) ? value : null;
    }

    /// <summary>
    /// The normalized origin of a document URI as WebView2 reports it
    /// (<c>Source</c> / <c>Uri</c>), or null when it is not an https document.
    /// </summary>
    public static string? FromUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed)) return null;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrEmpty(parsed.Host)) return null;
        var host = parsed.IdnHost;
        var origin = parsed.IsDefaultPort
            ? "https://" + host
            : "https://" + host + ":" + parsed.Port.ToString(CultureInfo.InvariantCulture);
        return Normalize(origin);
    }

    /// <summary>
    /// The identity allow-list: exactly https://&lt;assistant&gt;.busymate.ai plus the
    /// exact https origins given. Invalid entries are ignored, never widened.
    /// </summary>
    public static IReadOnlyList<string> ForAssistant(string? assistant, IEnumerable<string>? origins)
    {
        var list = new List<string>();
        if (IsAssistantSlug(assistant)) list.Add("https://" + assistant + ".busymate.ai");
        AddExact(list, origins);
        return list.AsReadOnly();
    }

    /// <summary>The exact, normalized, de-duplicated https origins; invalid entries are dropped.</summary>
    public static IReadOnlyList<string> Exact(IEnumerable<string>? origins)
    {
        var list = new List<string>();
        AddExact(list, origins);
        return list.AsReadOnly();
    }

    private static void AddExact(List<string> list, IEnumerable<string>? origins)
    {
        if (origins is null) return;
        foreach (var origin in origins)
        {
            if (origin is null || origin.Contains('*')) continue;
            var normalized = Normalize(origin);
            if (normalized is not null && !list.Contains(normalized)) list.Add(normalized);
        }
    }
}
