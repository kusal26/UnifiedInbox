using Microsoft.Extensions.Configuration;

namespace UnifiedInbox.Infrastructure.Configuration;

/// <summary>
/// Shared fail-closed validation for production boots of the API and the worker. Every process
/// that touches WhatsApp credentials must refuse to run with fake providers, missing Meta app
/// configuration, or weak/invalid cryptographic keys when the environment is Production.
/// </summary>
public static class ProductionGuard
{
    public static void Validate(IConfiguration configuration, bool isProduction)
    {
        if (!isProduction) return;
        var whatsapp = configuration["WhatsApp:UseFake"] ?? Environment.GetEnvironmentVariable("WHATSAPP_USE_FAKE");
        if (string.Equals(whatsapp, "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fake WhatsApp provider mode is forbidden in Production.");
        RequireSecret(configuration, "WhatsApp:AppId", "WHATSAPP_APP_ID", "WhatsApp:AppId is required in Production.");
        RequireSecret(configuration, "WhatsApp:EmbeddedSignupConfigId", "WHATSAPP_EMBEDDED_SIGNUP_CONFIG_ID", "WhatsApp:EmbeddedSignupConfigId is required in Production.");
        RequireSecret(configuration, "WhatsApp:AppSecret", "WHATSAPP_APP_SECRET", "WhatsApp:AppSecret is required in Production.");
        RequireSecret(configuration, "WhatsApp:VerifyToken", "WHATSAPP_VERIFY_TOKEN", "WhatsApp:VerifyToken is required in Production.");
        RequireKeyBytes(configuration, "Credentials:MasterKey", "CREDENTIAL_MASTER_KEY", "Credentials:MasterKey", required: true);
        RequireKeyBytes(configuration, "Credentials:PreviousMasterKey", "CREDENTIAL_PREVIOUS_MASTER_KEY", "Credentials:PreviousMasterKey", required: false);
        var jwt = configuration["Jwt:SigningKey"] ?? "";
        if (jwt.Length < 32 || jwt == "development-only-signing-key-change-before-production")
            throw new InvalidOperationException("Jwt:SigningKey must be a production secret (32+ chars).");
    }

    private static void RequireSecret(IConfiguration configuration, string key, string environment, string message)
    {
        var value = configuration[key] ?? Environment.GetEnvironmentVariable(environment);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(message);
    }

    /// <summary>Requires the key to decode to exactly 32 bytes. The active master key is mandatory;
    /// the previous key is optional but, when supplied, must be a valid 32-byte key as well.</summary>
    private static void RequireKeyBytes(IConfiguration configuration, string key, string environment, string label, bool required)
    {
        var raw = configuration[key] ?? Environment.GetEnvironmentVariable(environment);
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (required) throw new InvalidOperationException($"{label} is required in Production.");
            return;
        }
        try
        {
            if (Convert.FromBase64String(raw).Length != 32)
                throw new InvalidOperationException($"{label} must decode to exactly 32 bytes in Production.");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{label} must be valid base64 in Production.");
        }
    }
}
