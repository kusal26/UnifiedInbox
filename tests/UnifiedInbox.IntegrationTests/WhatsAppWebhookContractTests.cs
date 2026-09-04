using System.Text.Json;
using Shouldly;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;

namespace UnifiedInbox.IntegrationTests;

public sealed class WhatsAppWebhookContractTests
{
    private static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Status_callbacks_update_delivery_state()
    {
        var parsed = new WhatsAppPayloadParser().ParseFull(Doc("""
            {"entry":[{"changes":[{"value":{
              "metadata":{"phone_number_id":"phone-1"},
              "statuses":[{"id":"wamid.9","status":"delivered","timestamp":"1724000000"}]
            }}]}]}
            """));
        parsed.Messages.ShouldBeEmpty();
        var status = parsed.Statuses.ShouldHaveSingleItem();
        status.ExternalMessageId.ShouldBe("wamid.9");
        status.Status.ShouldBe("delivered");
        status.OccurredAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1724000000));
    }

    [Fact]
    public void Media_messages_carry_media_id_mime_kind_and_caption()
    {
        var parsed = new WhatsAppPayloadParser().ParseFull(Doc("""
            {"entry":[{"changes":[{"value":{
              "messages":[{"id":"wamid.2","from":"15550001","image":{"id":"media-9","mime_type":"image/jpeg","caption":"look"}}]
            }}]}]}
            """));
        var message = parsed.Messages.ShouldHaveSingleItem();
        message.MediaId.ShouldBe("media-9");
        message.DeclaredMimeType.ShouldBe("image/jpeg");
        message.Kind.ShouldBe(WhatsAppInboundKind.Image);
        message.Text.ShouldBe("look");
    }

    [Fact]
    public void Video_and_document_messages_carry_their_kind_and_file_name()
    {
        var parsed = new WhatsAppPayloadParser().ParseFull(Doc("""
            {"entry":[{"changes":[{"value":{"messages":[
              {"id":"wamid.v","from":"15550001","video":{"id":"media-v","mime_type":"video/mp4"}},
              {"id":"wamid.d","from":"15550001","document":{"id":"media-d","mime_type":"application/pdf","filename":"brief.pdf"}}
            ]}}]}]}
            """));
        var video = parsed.Messages[0];
        video.Kind.ShouldBe(WhatsAppInboundKind.Video);
        video.MediaId.ShouldBe("media-v");
        var document = parsed.Messages[1];
        document.Kind.ShouldBe(WhatsAppInboundKind.Document);
        document.FileName.ShouldBe("brief.pdf");
    }

    [Fact]
    public void Audio_and_stickers_are_parsed_but_never_ingested()
    {
        var parsed = new WhatsAppPayloadParser().ParseFull(Doc("""
            {"entry":[{"changes":[{"value":{"messages":[
              {"id":"wamid.a","from":"15550001","audio":{"id":"media-a","mime_type":"audio/ogg"}},
              {"id":"wamid.s","from":"15550001","sticker":{"id":"media-s","mime_type":"image/webp"}}
            ]}}]}]}
            """));
        parsed.Messages[0].Kind.ShouldBe(WhatsAppInboundKind.Audio);
        parsed.Messages[1].Kind.ShouldBe(WhatsAppInboundKind.Sticker);
    }

    [Fact]
    public void Provider_errors_and_unknown_events_are_ignored_without_throwing()
    {
        var parser = new WhatsAppPayloadParser();
        parser.ParseFull(Doc("""{"entry":[{"changes":[{"value":{"messages":[],"errors":[{"code":131000}]}}]}]}""")).Messages.ShouldBeEmpty();
        parser.ParseFull(Doc("""{"object":"whatsapp_business_account","entry":[{"changes":[{"field":"unknown_field","value":{}}]}]}""")).Messages.ShouldBeEmpty();
        parser.ParseFull(Doc("""{"unexpected":"shape"}""")).Messages.ShouldBeEmpty();
        parser.ParseFull(Doc("[]")).Messages.ShouldBeEmpty();
    }

    [Fact]
    public void Malformed_siblings_never_drop_well_formed_messages()
    {
        var parsed = new WhatsAppPayloadParser().ParseFull(Doc("""
            {"entry":[{"changes":[{"value":{
              "messages":[{"from":"15550001","text":{"body":"broken"}},{"id":"wamid.3","from":"15550002","text":{"body":"fine"}}]
            }}]}]}
            """));
        parsed.Messages.ShouldHaveSingleItem().ExternalMessageId.ShouldBe("wamid.3");
    }

    [Fact]
    public void Subscription_verification_requires_exact_mode_and_token()
    {
        var configuration = new DictionaryConfiguration(new Dictionary<string, string?> { ["WhatsApp:VerifyToken"] = "tok-123" });
        var controller = new UnifiedInbox.Api.Controllers.WebhooksController(null!, new WhatsAppSignatureValidator(), configuration);

        var ok = controller.Verify("subscribe", "tok-123", "challenge-1") as Microsoft.AspNetCore.Mvc.ContentResult;
        ok.ShouldNotBeNull();
        ok.Content.ShouldBe("challenge-1");

        controller.Verify("subscribe", "wrong", "challenge-1").ShouldBeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
        controller.Verify("unsubscribe", "tok-123", "challenge-1").ShouldBeOfType<Microsoft.AspNetCore.Mvc.ForbidResult>();
    }

    [Fact]
    public void Flat_test_envelopes_still_parse()
    {
        var parsed = new WhatsAppPayloadParser().ParseFull(Doc("""{"messages":[{"id":"wamid.4","from":"15550003","text":{"body":"hi"}}]}"""));
        parsed.Messages.ShouldHaveSingleItem().CustomerId.ShouldBe("15550003");
    }
}
