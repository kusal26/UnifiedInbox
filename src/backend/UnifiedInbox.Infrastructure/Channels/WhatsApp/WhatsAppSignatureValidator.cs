using System.Security.Cryptography;
using System.Text;

namespace UnifiedInbox.Infrastructure.Channels.WhatsApp;

public sealed class WhatsAppSignatureValidator
{
    public bool IsValid(ReadOnlySpan<byte> body, string? signature, string appSecret)
    {
        if (string.IsNullOrWhiteSpace(signature) || !signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body);
        try { var actual = Convert.FromHexString(signature[7..]); return CryptographicOperations.FixedTimeEquals(expected, actual); }
        catch (FormatException) { return false; }
    }
}
