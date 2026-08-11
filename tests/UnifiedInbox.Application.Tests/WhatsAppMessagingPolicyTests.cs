using Shouldly;
using UnifiedInbox.Application;

namespace UnifiedInbox.Application.Tests;

public sealed class WhatsAppMessagingPolicyTests
{
    [Fact]
    public void Allows_freeform_inside_customer_service_window() => new WhatsAppMessagingPolicy().Evaluate(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, false).ShouldBe(WhatsAppSendDecision.AllowedFreeform);
    [Fact]
    public void Requires_template_outside_window() => new WhatsAppMessagingPolicy().Evaluate(DateTimeOffset.UtcNow.AddHours(-25), DateTimeOffset.UtcNow, false).ShouldBe(WhatsAppSendDecision.TemplateRequired);
}
