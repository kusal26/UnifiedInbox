using System.Security.Cryptography;
using System.Text;
using Shouldly;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;

namespace UnifiedInbox.IntegrationTests;

public sealed class WhatsAppContractTests
{
    [Fact]
    public void Signature_validation_uses_constant_time_comparison() { var body = Encoding.UTF8.GetBytes("{}"); var secret = "test-secret"; var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)); new WhatsAppSignatureValidator().IsValid(body, signature, secret).ShouldBeTrue(); }
}
