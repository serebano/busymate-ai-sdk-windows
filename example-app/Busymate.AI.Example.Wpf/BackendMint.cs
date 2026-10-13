using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Busymate.AI.Example;

/// <summary>
/// Calls YOUR authenticated backend mint endpoint with the SDK's mint request. Redirects are
/// refused so the app session credential is never forwarded. Nothing here fakes a success.
/// </summary>
public static class BackendMint
{
    private static readonly Lazy<HttpClient> Shared = new(() => CreateClient(CreateHandler()));

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    internal static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Accepts only a non-empty token whose nonce is the one the chat asked to sign.</summary>
    internal static BusymateBridge.MintResult Reply(string? token, string? nonce, object? expectedNonce)
    {
        if (string.IsNullOrEmpty(token) || nonce is null || !Equals(nonce, expectedNonce))
            throw new InvalidOperationException("Backend reply does not match the requested nonce.");
        return BusymateBridge.MintResult.Success(token, nonce);
    }

    public static async Task<BusymateBridge.MintResult> MintAsync(
        DemoSettings settings,
        bool signedIn,
        IReadOnlyDictionary<string, object?> request,
        CancellationToken cancellation,
        HttpClient? client = null)
    {
        if (!signedIn || settings.AccountId.Length == 0) return BusymateBridge.MintResult.NotSignedIn;
        if (settings.BackendUrl.Length == 0) return BusymateBridge.MintResult.Failed;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, settings.BackendUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"),
            };
            if (settings.BackendBearer.Length > 0)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.BackendBearer);
            using var response = await (client ?? Shared.Value).SendAsync(message, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return BusymateBridge.MintResult.NotSignedIn;
            if (!response.IsSuccessStatusCode) return BusymateBridge.MintResult.Failed; // 3xx included: never followed
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var token = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var nonce = root.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            request.TryGetValue("nonce", out var expected);
            return Reply(token, nonce, expected);
        }
        catch (Exception)
        {
            return BusymateBridge.MintResult.Failed;
        }
    }
}
