namespace UnifiedInbox.Application;

public enum WhatsAppSendDecision { AllowedFreeform, TemplateRequired, UnsupportedMedia, ReauthorizationRequired, ProviderRateLimited }

public sealed class WhatsAppMessagingPolicy
{
    public WhatsAppSendDecision Evaluate(DateTimeOffset? lastInboundAt, DateTimeOffset now, bool hasApprovedTemplate)
    {
        if (lastInboundAt is not null && now - lastInboundAt.Value <= TimeSpan.FromHours(24)) return WhatsAppSendDecision.AllowedFreeform;
        return hasApprovedTemplate ? WhatsAppSendDecision.AllowedFreeform : WhatsAppSendDecision.TemplateRequired;
    }
}
