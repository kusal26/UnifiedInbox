using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    public async Task<IReadOnlyList<GraphPhoneNumber>> GetPhoneNumbersAsync(string businessId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = GraphGet($"{businessId}/phone_numbers?fields=id,display_phone_number,code_verification_status,quality_rating", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureGraphSuccess(response, "WABA phone-number lookup", cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        if (!body.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var phones = new List<GraphPhoneNumber>();
        foreach (var item in data.EnumerateArray())
        {
            var id = Property(item, "id");
            if (id.Length == 0) continue;
            phones.Add(new GraphPhoneNumber(id, Property(item, "display_phone_number"), Property(item, "code_verification_status")));
        }
        return phones;
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

    public async Task<IReadOnlyList<WhatsAppTemplateInfo>> ListMessageTemplatesAsync(string businessId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = GraphGet($"{businessId}/message_templates?fields=name,language,status,category,components&status=APPROVED&limit=1000", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureGraphSuccess(response, "template lookup", cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        if (!body.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var templates = new List<WhatsAppTemplateInfo>();
        foreach (var item in data.EnumerateArray())
        {
            var name = Property(item, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var components = new List<WhatsAppTemplateComponentInfo>();
            if (item.TryGetProperty("components", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                foreach (var component in nodes.EnumerateArray())
                {
                    var type = Property(component, "type").ToUpperInvariant();
                    if (type.Length == 0) continue;
                    components.Add(new WhatsAppTemplateComponentInfo(type, ParameterCount(component, type)));
                }
            templates.Add(new WhatsAppTemplateInfo(name, Property(item, "language"), Property(item, "category"), Property(item, "status"), components));
        }
        return templates;
    }

    /// <summary>Number of parameters a component needs: body/header text placeholders, or a single
    /// media parameter for a non-text header (image/video/document).</summary>
    private static int ParameterCount(JsonElement component, string type)
    {
        if (type == "HEADER")
        {
            var format = Property(component, "format");
            if (format.Length > 0 && !string.Equals(format, "TEXT", StringComparison.OrdinalIgnoreCase)) return 1;
        }
        if (type is not ("BODY" or "HEADER")) return 0;
        var text = Property(component, "text");
        return Regex.Matches(text, @"\{\{\s*\d+\s*\}\}").Count;
    }

    private static string Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public async Task<GraphMediaMetadata> GetMediaAsync(string mediaId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = GraphGet($"{mediaId}?fields=url,mime_type,file_size,sha256", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureGraphSuccess(response, "media lookup", cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        var root = body.RootElement;
        var url = Property(root, "url");
        if (string.IsNullOrWhiteSpace(url)) throw new InboxException("provider_error", "The provider did not return a media url.", 502);
        var mime = Property(root, "mime_type");
        long? size = null;
        if (root.TryGetProperty("file_size", out var rawSize) && long.TryParse(rawSize.GetString(), out var parsed)) size = parsed;
        return new GraphMediaMetadata(url, mime, size);
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
