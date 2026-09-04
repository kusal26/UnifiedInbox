using UnifiedInbox.Domain;

namespace UnifiedInbox.Application.Messaging;

/// <summary>A resolved provider message for one delivery part. Text, an approved template, and
/// media sent by provider id are distinct Graph message shapes that share recipient and type.</summary>
public abstract record WhatsAppSendPayload(string To, DeliveryPartKind Kind);

public sealed record WhatsAppTextPayload(string To, string Body) : WhatsAppSendPayload(To, DeliveryPartKind.Text);

public sealed record WhatsAppTemplatePayload(string To, string Name, string Language, string? ComponentsJson = null) : WhatsAppSendPayload(To, DeliveryPartKind.Template);

public sealed record WhatsAppMediaPayload(string To, DeliveryPartKind Kind, string MediaId) : WhatsAppSendPayload(To, Kind);
