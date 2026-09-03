using System.Security.Cryptography;
using Shouldly;
using UnifiedInbox.Infrastructure.Security;

namespace UnifiedInbox.IntegrationTests;

public sealed class CredentialProtectorTests
{
    private static readonly byte[] Key = SHA256.HashData("deployment master key"u8.ToArray());

    [Fact]
    public void Versioned_envelope_round_trips()
    {
        var protector = new CredentialProtector(Key);
        var envelope = protector.Protect("secret-token");

        envelope.ShouldStartWith("v1.");
        protector.Unprotect(envelope).ShouldBe("secret-token");
    }

    [Fact]
    public void Modified_ciphertext_is_rejected()
    {
        var protector = new CredentialProtector(Key);
        var envelope = protector.Protect("secret-token");
        var replacement = envelope[^1] == 'A' ? 'B' : 'A';

        Should.Throw<CryptographicException>(() => protector.Unprotect(envelope[..^1] + replacement));
    }
}
