using System.Text.Json;
using Shouldly;
using UnifiedInbox.Application.Messaging;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Application.Tests;

public sealed class OutboundMessageCommandTests
{
    private static readonly Guid AttachmentA = Guid.NewGuid();
    private static readonly Guid AttachmentB = Guid.NewGuid();

    [Fact]
    public void Free_form_body_inside_window_plans_a_single_text_part()
    {
        var command = new OutboundMessageCommand("hello", "key-1");
        var parts = OutboundMessagePlanner.Plan(command, new Dictionary<Guid, string>());

        var part = parts.ShouldHaveSingleItem();
        part.Kind.ShouldBe(DeliveryPartKind.Text);
        part.AttachmentId.ShouldBeNull();
        part.TemplateName.ShouldBeNull();
    }

    [Fact]
    public void Approved_template_plans_a_single_template_part_with_snapshot()
    {
        var command = new OutboundMessageCommand("", "key-2", Template: new OutboundTemplate("order_shipping", "en_US"));
        var parts = OutboundMessagePlanner.Plan(command, new Dictionary<Guid, string>());

        var part = parts.ShouldHaveSingleItem();
        part.Kind.ShouldBe(DeliveryPartKind.Template);
        part.TemplateName.ShouldBe("order_shipping");
        part.TemplateLanguage.ShouldBe("en_US");
        part.TemplateComponentsJson.ShouldBeNull();
    }

    [Fact]
    public void Approved_template_persists_its_parameter_snapshot()
    {
        var component = JsonSerializer.Deserialize<JsonElement>("""{"type":"body","parameters":[{"type":"text","text":"order 42"}]}""");
        var command = new OutboundMessageCommand("", "key-3", Template: new OutboundTemplate("order_shipping", "en_US", new[] { component }));
        var part = OutboundMessagePlanner.Plan(command, new Dictionary<Guid, string>()).ShouldHaveSingleItem();

        part.Kind.ShouldBe(DeliveryPartKind.Template);
        using var snapshot = JsonDocument.Parse(part.TemplateComponentsJson!);
        snapshot.RootElement[0].GetProperty("parameters")[0].GetProperty("text").GetString().ShouldBe("order 42");
    }

    [Fact]
    public void Body_plus_two_attachments_plans_three_ordered_parts()
    {
        var command = new OutboundMessageCommand("here are the files", "key-4", new[] { AttachmentA, AttachmentB });
        var parts = OutboundMessagePlanner.Plan(command, new Dictionary<Guid, string>
        {
            [AttachmentA] = "image/jpeg",
            [AttachmentB] = "video/mp4",
        });

        parts.Count.ShouldBe(3);
        parts[0].Kind.ShouldBe(DeliveryPartKind.Text);
        parts[0].AttachmentId.ShouldBeNull();
        parts[1].Kind.ShouldBe(DeliveryPartKind.Image);
        parts[1].AttachmentId.ShouldBe(AttachmentA);
        parts[2].Kind.ShouldBe(DeliveryPartKind.Video);
        parts[2].AttachmentId.ShouldBe(AttachmentB);
    }

    [Fact]
    public void Attachments_without_a_body_plan_only_media_parts()
    {
        var command = new OutboundMessageCommand("", "key-5", new[] { AttachmentA });
        var parts = OutboundMessagePlanner.Plan(command, new Dictionary<Guid, string> { [AttachmentA] = "application/pdf" });

        var part = parts.ShouldHaveSingleItem();
        part.Kind.ShouldBe(DeliveryPartKind.Document);
        part.AttachmentId.ShouldBe(AttachmentA);
    }

    [Fact]
    public void A_template_never_adds_a_free_form_text_part()
    {
        var command = new OutboundMessageCommand("typed but un-sendable", "key-6", Template: new OutboundTemplate("welcome", "en_GB"));
        var parts = OutboundMessagePlanner.Plan(command, new Dictionary<Guid, string>());

        parts.ShouldHaveSingleItem().Kind.ShouldBe(DeliveryPartKind.Template);
    }

    [Theory]
    [InlineData("image/jpeg", DeliveryPartKind.Image)]
    [InlineData("image/png", DeliveryPartKind.Image)]
    [InlineData("IMAGE/GIF", DeliveryPartKind.Image)]
    [InlineData("video/mp4", DeliveryPartKind.Video)]
    [InlineData("application/pdf", DeliveryPartKind.Document)]
    [InlineData("application/octet-stream", DeliveryPartKind.Document)]
    public void Content_types_map_to_delivery_part_kinds(string contentType, DeliveryPartKind expected) =>
        OutboundMessagePlanner.KindFor(contentType).ShouldBe(expected);
}
