using System.Text.Json;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Application.Messaging;

/// <summary>
/// The requested send shape for an outbound message. A free-form message is a body inside the
/// 24-hour window; outside the window the caller must supply an approved template identity so the
/// request is never treated as proof of approval on name alone.
/// </summary>
public sealed record OutboundMessageCommand(
    string Body,
    string IdempotencyKey,
    IReadOnlyList<Guid>? AttachmentIds = null,
    OutboundTemplate? Template = null);

/// <summary>An approved template identity and the exact parameter values to substitute.</summary>
public sealed record OutboundTemplate(
    string Name,
    string Language,
    IReadOnlyList<JsonElement>? Components = null);

/// <summary>One durable delivery-part to create for a requested send.</summary>
public sealed record DeliveryPartSpec(
    DeliveryPartKind Kind,
    Guid? AttachmentId = null,
    string? TemplateName = null,
    string? TemplateLanguage = null,
    string? TemplateComponentsJson = null);

/// <summary>
/// Turns the requested send shape into an ordered list of delivery parts. A template send produces
/// exactly one template part (the provider owns the content); otherwise a body produces a leading
/// text part followed by one part per attachment in the order supplied.
/// </summary>
public static class OutboundMessagePlanner
{
    public static IReadOnlyList<DeliveryPartSpec> Plan(OutboundMessageCommand command, IReadOnlyDictionary<Guid, string> attachmentContentTypes)
    {
        if (command.Template is { } template)
        {
            var components = template.Components is { Count: > 0 } ? JsonSerializer.Serialize(template.Components) : null;
            return [new DeliveryPartSpec(DeliveryPartKind.Template, TemplateName: template.Name, TemplateLanguage: template.Language, TemplateComponentsJson: components)];
        }

        var parts = new List<DeliveryPartSpec>();
        if (!string.IsNullOrWhiteSpace(command.Body)) parts.Add(new DeliveryPartSpec(DeliveryPartKind.Text));
        if (command.AttachmentIds is { Count: > 0 })
            foreach (var attachmentId in command.AttachmentIds)
                parts.Add(new DeliveryPartSpec(KindFor(ContentTypeFor(attachmentContentTypes, attachmentId)), AttachmentId: attachmentId));
        return parts;
    }

    public static DeliveryPartKind KindFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        var type when type.StartsWith("image/", StringComparison.Ordinal) => DeliveryPartKind.Image,
        "video/mp4" => DeliveryPartKind.Video,
        _ => DeliveryPartKind.Document,
    };

    private static string ContentTypeFor(IReadOnlyDictionary<Guid, string> contentTypes, Guid attachmentId) =>
        contentTypes.TryGetValue(attachmentId, out var contentType) ? contentType : "";
}
