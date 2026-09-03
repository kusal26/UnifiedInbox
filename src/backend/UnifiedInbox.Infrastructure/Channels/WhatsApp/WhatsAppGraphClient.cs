using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using UnifiedInbox.Application;

namespace UnifiedInbox.Infrastructure.Channels.WhatsApp;

public sealed class WhatsAppGraphClient(HttpClient http, IConfiguration configuration) : IWhatsAppGraphClient
{
    private string Version => configuration["WhatsApp:GraphVersion"] ?? Environment.GetEnvironmentVariable("WHATSAPP_GRAPH_VERSION") ?? "v23.0";
    private string AppId => configuration["WhatsApp:AppId"] ?? Environment.GetEnvironmentVariable("WHATSAPP_APP_ID") ?? throw new InvalidOperationException("WhatsApp:AppId is required for onboarding.");
    private string AppSecret => configuration["WhatsApp:AppSecret"] ?? Environment.GetEnvironmentVariable("WHATSAPP_APP_SECRET") ?? throw new InvalidOperationException("WhatsApp:AppSecret is required for onboarding.");

    public async Task<string> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        var redirectUri = configuration["WhatsApp:RedirectUri"] ?? Environment.GetEnvironmentVariable("WHATSAPP_REDIRECT_URI");
        var url = $"https://graph.facebook.com/{Version}/oauth/access_token?client_id={Uri.EscapeDataString(AppId)}&client_secret={Uri.EscapeDataString(AppSecret)}&code={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrWhiteSpace(redirectUri)) url += $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";
        using var response = await http.GetAsync(url, cancellationToken);
        await EnsureGraphSuccess(response, "code exchange", cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        return body.RootElement.GetProperty("access_token").GetString() ?? throw new InboxException("provider_error", "The provider did not return an access token.", 502);
    }

    public async Task<GraphPhoneNumber> GetPhoneNumberAsync(string phoneNumberId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = GraphGet($"{phoneNumberId}?fields=id,display_phone_number,code_verification_status,quality_rating", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureGraphSuccess(response, "phone number lookup", cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        var root = body.RootElement;
        return new(
            root.GetProperty("id").GetString() ?? phoneNumberId,
            root.TryGetProperty("display_phone_number", out var number) ? number.GetString() ?? "" : "",
            root.TryGetProperty("code_verification_status", out var status) ? status.GetString() ?? "" : "");
    }

    public async Task<string> GetBusinessNameAsync(string businessId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = GraphGet($"{businessId}?fields=id,name", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureGraphSuccess(response, "business lookup", cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        return body.RootElement.TryGetProperty("name", out var name) ? name.GetString() ?? businessId : businessId;
    }

    public async Task<IReadOnlyList<string>> GetTokenScopesAsync(string accessToken, CancellationToken cancellationToken)
    {
        var url = $"https://graph.facebook.com/{Version}/debug_token?input_token={Uri.EscapeDataString(accessToken)}&access_token={Uri.EscapeDataString(AppId)}|{Uri.EscapeDataString(AppSecret)}";
        using var response = await http.GetAsync(url, cancellationToken);
        await EnsureGraphSuccess(response, "scope validation", cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        if (body.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array)
            return scopes.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
        return [];
    }

    public async Task SubscribeAppAsync(string businessId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{Version}/{businessId}/subscribed_apps");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureGraphSuccess(response, "webhook subscription", cancellationToken);
    }

    public async Task UnsubscribeAppAsync(string businessId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"https://graph.facebook.com/{Version}/{businessId}/subscribed_apps");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureGraphSuccess(response, "webhook unsubscription", cancellationToken);
    }

    private HttpRequestMessage GraphGet(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://graph.facebook.com/{Version}/{path}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task EnsureGraphSuccess(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string detail;
        try { detail = await response.Content.ReadAsStringAsync(cancellationToken); }
        catch { detail = response.StatusCode.ToString(); }
        if ((int)response.StatusCode is 401 or 403)
            throw new InboxException("provider_unauthorized", $"The provider rejected the {operation} (revoked or invalid access).", 502);
        throw new InboxException("provider_error", $"The provider {operation} failed ({(int)response.StatusCode}).", 502);
    }
}
