using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace UnifiedInbox.Infrastructure.Persistence;

public static class TenantToken
{
    public static string Create(Guid tenantId, int randomBytes = 32)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant id is required.", nameof(tenantId));
        return $"v1.{WebEncoders.Base64UrlEncode(tenantId.ToByteArray())}.{WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(randomBytes))}";
    }

    public static bool TryGetTenantId(string? token, out Guid tenantId)
    {
        tenantId = default;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != "v1" || parts[2].Length == 0) return false;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(parts[1]);
            if (bytes.Length != 16) return false;
            tenantId = new Guid(bytes);
            return tenantId != Guid.Empty;
        }
        catch (FormatException) { return false; }
    }
}
