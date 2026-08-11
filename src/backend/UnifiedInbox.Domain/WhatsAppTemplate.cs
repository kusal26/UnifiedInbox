namespace UnifiedInbox.Domain;

public sealed record WhatsAppTemplate(Guid Id, Guid TenantId, string Name, string Language, IReadOnlyList<string> Components, bool Approved);
