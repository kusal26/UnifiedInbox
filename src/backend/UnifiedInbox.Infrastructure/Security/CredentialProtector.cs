using System.Security.Cryptography;
using System.Text;

namespace UnifiedInbox.Infrastructure.Security;

public sealed class CredentialProtector
{
    private readonly byte[] key;
    private readonly byte[]? previousKey;
    public CredentialProtector(byte[] key, byte[]? previousKey = null)
    {
        if (key.Length != 32) throw new ArgumentException("The credential master key must be 32 bytes.", nameof(key));
        if (previousKey is not null && previousKey.Length != 32) throw new ArgumentException("The previous master key must be 32 bytes.", nameof(previousKey));
        this.key = key.ToArray();
        this.previousKey = previousKey?.ToArray();
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var input = Encoding.UTF8.GetBytes(plaintext); var ciphertext = new byte[input.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length); aes.Encrypt(nonce, input, ciphertext, tag, "unified-inbox:v1"u8.ToArray());
        return $"v1.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(ciphertext)}.{Convert.ToBase64String(tag)}";
    }

    public string Unprotect(string envelope)
    {
        var parts = envelope.Split('.'); if (parts.Length != 4 || parts[0] != "v1") throw new CryptographicException("Unsupported credential envelope.");
        try { return Decrypt(key, parts); }
        catch (CryptographicException) when (previousKey is not null)
        {
            try { return Decrypt(previousKey, parts); }
            catch (CryptographicException) { throw new CryptographicException("Invalid credential envelope."); }
        }
    }

    /// <summary>True when the envelope only decrypts with the previous key and should be re-encrypted.</summary>
    public bool NeedsRotation(string envelope)
    {
        if (previousKey is null) return false;
        var parts = envelope.Split('.');
        if (parts.Length != 4 || parts[0] != "v1") return false;
        try { Decrypt(key, parts); return false; }
        catch { try { Decrypt(previousKey, parts); return true; } catch { return false; } }
    }

    private static string Decrypt(byte[] keyBytes, string[] parts)
    {
        try { var nonce = Convert.FromBase64String(parts[1]); var ciphertext = Convert.FromBase64String(parts[2]); var tag = Convert.FromBase64String(parts[3]); var plaintext = new byte[ciphertext.Length]; using var aes = new AesGcm(keyBytes, tag.Length); aes.Decrypt(nonce, ciphertext, tag, plaintext, "unified-inbox:v1"u8.ToArray()); return Encoding.UTF8.GetString(plaintext); }
        catch (FormatException exception) { throw new CryptographicException("Invalid credential envelope.", exception); }
    }
}
