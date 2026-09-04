using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Shouldly;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Channels.WhatsApp;
using UnifiedInbox.Infrastructure.Persistence;
using UnifiedInbox.Infrastructure.Security;

namespace UnifiedInbox.IntegrationTests;

/// <summary>
/// Graph HTTP contract tests: the outbound sender must POST exact text/template payloads and map
/// provider failures to stable, retry-aware outcomes; the template catalog must request only
/// APPROVED templates and return a sanitized shape.
/// </summary>
public sealed class WhatsAppTemplateContractTests
{
    private static readonly string MasterKey = Convert.ToBase64String(SHA256.HashData("template-master"u8.ToArray()));
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public async Task Text_payload_posts_a_text_message()
    {
        using var handler = new StubHandler(Ok("""{"messages":[{"id":"wamid.text"}]}"""));
        using var harness = Sender(handler);
        var part = Part(DeliveryPartKind.Text);

        (await harness.Sender.SendPartAsync(harness.Db, harness.Channel, harness.Contact, "hello there", part, CancellationToken.None)).ShouldBe("wamid.text");

        var request = handler.Requests.ShouldHaveSingleItem();
        request.RequestUri!.AbsolutePath.ShouldEndWith($"/{harness.Channel.ExternalAccountId}/messages");
        var body = JsonDocument.Parse(handler.Bodies[0]);
        body.RootElement.GetProperty("type").GetString().ShouldBe("text");
        body.RootElement.GetProperty("text").GetProperty("body").GetString().ShouldBe("hello there");
        body.RootElement.GetProperty("to").GetString().ShouldBe("+15550001");
    }

    [Fact]
    public async Task Template_payload_posts_name_language_and_components()
    {
        using var handler = new StubHandler(Ok("""{"messages":[{"id":"wamid.tpl"}]}"""));
        using var harness = Sender(handler);
        var part = Part(DeliveryPartKind.Template, templateName: "order_shipping", templateLanguage: "en_US",
            componentsJson: """[{"type":"body","parameters":[{"type":"text","text":"order 42"}]}]""");

        (await harness.Sender.SendPartAsync(harness.Db, harness.Channel, harness.Contact, "", part, CancellationToken.None)).ShouldBe("wamid.tpl");

        var request = handler.Requests.ShouldHaveSingleItem();
        var body = JsonDocument.Parse(handler.Bodies[0]);
        body.RootElement.GetProperty("type").GetString().ShouldBe("template");
        body.RootElement.GetProperty("template").GetProperty("name").GetString().ShouldBe("order_shipping");
        body.RootElement.GetProperty("template").GetProperty("language").GetProperty("code").GetString().ShouldBe("en_US");
        body.RootElement.GetProperty("template").GetProperty("components")[0].GetProperty("parameters")[0].GetProperty("text").GetString().ShouldBe("order 42");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "channel_authorization_expired")]
    [InlineData(HttpStatusCode.Forbidden, "channel_authorization_expired")]
    [InlineData(HttpStatusCode.TooManyRequests, "provider_rate_limited")]
    public async Task Provider_authorization_and_rate_limit_failures_map_to_stable_codes(HttpStatusCode status, string code)
    {
        using var handler = new StubHandler(new HttpResponseMessage(status));
        using var harness = Sender(handler);
        var failure = await Should.ThrowAsync<InboxException>(() => harness.Sender.SendPartAsync(harness.Db, harness.Channel, harness.Contact, "hello", Part(DeliveryPartKind.Text), CancellationToken.None));
        failure.Code.ShouldBe(code);
        if (code == "provider_rate_limited") OutboxRetryPolicy.IsTransient(failure).ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Other_5xx_failures_are_transient(HttpStatusCode status)
    {
        using var handler = new StubHandler(new HttpResponseMessage(status));
        using var harness = Sender(handler);
        var failure = await Should.ThrowAsync<InboxException>(() => harness.Sender.SendPartAsync(harness.Db, harness.Channel, harness.Contact, "hello", Part(DeliveryPartKind.Text), CancellationToken.None));
        failure.Code.ShouldBe("provider_temporarily_unavailable");
        OutboxRetryPolicy.IsTransient(failure).ShouldBeTrue();
    }

    [Fact]
    public async Task Template_discovery_queries_approved_templates_and_sanitizes_the_result()
    {
        var graphBody = """{"data":[{"id":"1","name":"hello_world","language":"en_US","status":"APPROVED","category":"UTILITY","components":[{"type":"BODY","text":"Hello {{1}}, thanks for {{2}}!"}]},{"id":"2","name":"photo_tpl","language":"en_GB","status":"APPROVED","category":"MARKETING","components":[{"type":"HEADER","format":"IMAGE"},{"type":"BODY","text":"See this"}]}]}""";
        using var handler = new StubHandler(Ok(graphBody));
        using var http = new HttpClient(handler);
        var graph = new WhatsAppGraphClient(http, new DictionaryConfiguration(new Dictionary<string, string?> { ["WhatsApp:GraphVersion"] = "v99.0" }));

        var templates = await graph.ListMessageTemplatesAsync("waba-9", "graph-token", CancellationToken.None);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.RequestUri!.AbsolutePath.ShouldEndWith("/waba-9/message_templates");
        request.RequestUri.Query.ShouldContain("status=APPROVED");
        templates.Count.ShouldBe(2);
        templates[0].Name.ShouldBe("hello_world");
        templates[0].Language.ShouldBe("en_US");
        templates[0].Status.ShouldBe("APPROVED");
        templates[0].Components.ShouldContain(x => x.Type == "BODY" && x.ParameterCount == 2);
        templates[1].Components.ShouldContain(x => x.Type == "HEADER" && x.ParameterCount == 1);
    }

    [Fact]
    public async Task Template_discovery_never_exposes_tokens_or_raw_graph_fields()
    {
        var graphBody = """{"data":[{"id":"secret-waba","name":"hello_world","language":"en_US","status":"APPROVED","category":"UTILITY","components":[{"type":"BODY","text":"Hello {{1}}"}]}]}""";
        using var handler = new StubHandler(Ok(graphBody));
        using var http = new HttpClient(handler);
        var graph = new WhatsAppGraphClient(http, new DictionaryConfiguration([]));

        var serialized = JsonSerializer.Serialize(await graph.ListMessageTemplatesAsync("waba-9", "super-secret-token", CancellationToken.None));

        serialized.ShouldNotContain("super-secret-token");
        serialized.ShouldNotContain("secret-waba");
    }

    private static Harness Sender(StubHandler handler)
    {
        var (db, _) = TestContexts.Create(TenantId, Guid.NewGuid());
        var channel = new Channel(Guid.NewGuid(), TenantId, "whatsapp", "phone-1", true) { DisplayName = "Sales", ExternalBusinessId = "waba-9" };
        db.Channels.Add(channel);
        db.ChannelCredentials.Add(new ChannelCredential { TenantId = TenantId, ChannelId = channel.Id, EncryptedAccessToken = new CredentialProtector(Convert.FromBase64String(MasterKey)).Protect("provider-token") });
        var contact = new Contact(Guid.NewGuid(), TenantId, "whatsapp", "phone-1", "15550001", "C", "+15550001");
        db.Contacts.Add(contact);
        db.SaveChanges();
        var configuration = new DictionaryConfiguration(new Dictionary<string, string?> { ["Credentials:MasterKey"] = MasterKey });
        var sender = new WhatsAppMessageSender(new HttpClient(handler), configuration, new ProductionEnvironment());
        return new Harness(db, channel, contact, sender);
    }

    private static MessageDeliveryPart Part(DeliveryPartKind kind, string? templateName = null, string? templateLanguage = null, string? componentsJson = null) => new()
    {
        TenantId = TenantId,
        MessageId = Guid.NewGuid(),
        Position = 0,
        Kind = kind,
        TemplateName = templateName,
        TemplateLanguage = templateLanguage,
        TemplateComponentsJson = componentsJson,
        Status = MessageStatus.Pending,
    };

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Harness(InboxDbContext Db, Channel Channel, Contact Contact, WhatsAppMessageSender Sender) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        public StubHandler(params HttpResponseMessage[] responses) => this.responses = new Queue<HttpResponseMessage>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            var response = responses.Count > 0 ? responses.Dequeue() : Ok("""{"messages":[{"id":"wamid.1"}]}""");
            return response;
        }
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
