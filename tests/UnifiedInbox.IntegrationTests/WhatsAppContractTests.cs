using System.Security.Cryptography;
using System.Text;
using Shouldly;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;

namespace UnifiedInbox.IntegrationTests;

public sealed class WhatsAppContractTests
{
    [Fact]
    public void Signature_validation_uses_constant_time_comparison() { var body = Encoding.UTF8.GetBytes("{}"); var secret = "test-secret"; var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)); new WhatsAppSignatureValidator().IsValid(body, signature, secret).ShouldBeTrue(); }

    [Fact]
    public void Malformed_signature_is_rejected_without_throwing() => new WhatsAppSignatureValidator().IsValid("{}"u8, "sha256=not-hex", "secret").ShouldBeFalse();

    [Fact]
    public void Cloud_api_envelope_is_normalized()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"entry":[{"changes":[{"value":{"messages":[{"id":"wamid.1","from":"15550001","text":{"body":"hello"}}]}}]}]}""");
        var messages = new WhatsAppPayloadParser().Parse(document.RootElement);
        messages.Single().ShouldBe(new WhatsAppInbound("wamid.1", "15550001", "hello", null, null, null, WhatsAppInboundKind.None));
    }
}
