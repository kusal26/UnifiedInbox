using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shouldly;
using Testcontainers.RabbitMq;
using UnifiedInbox.Infrastructure.Messaging;

namespace UnifiedInbox.IntegrationTests;

/// <summary>Broker-level durability: canonical routing, redelivery, and publisher confirms.</summary>
public sealed class RabbitMqTopologyTests : IAsyncLifetime
{
    private readonly RabbitMqContainer container = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
    private IConnection connection = null!;
    private IChannel channel = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        var factory = new ConnectionFactory { Uri = new Uri(container.GetConnectionString()), AutomaticRecoveryEnabled = true };
        connection = await factory.CreateConnectionAsync();
        channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await RabbitMqTopology.DeclareAsync(channel);
    }

    public async Task DisposeAsync()
    {
        if (channel is not null) await channel.DisposeAsync();
        if (connection is not null) await connection.DisposeAsync();
        await container.DisposeAsync();
    }

    [DockerFact]
    public async Task Canonical_events_route_to_worker_and_realtime_queues()
    {
        await Publish("webhook.received", """{"receiptId":"00000000-0000-0000-0000-000000000001"}""");
        await Publish("outbound.message.requested", """{"messageId":"00000000-0000-0000-0000-000000000002"}""");
        await Publish("message.created", """{"id":"00000000-0000-0000-0000-000000000003"}""");

        (await GetBody("unified-inbox.worker")).ShouldContain("receiptId");
        (await GetBody("unified-inbox.worker")).ShouldContain("messageId");
        (await GetBody("unified-inbox.realtime")).ShouldContain("00000000-0000-0000-0000-000000000003");
    }

    [DockerFact]
    public async Task Nacked_deliveries_are_redelivered_with_message_id_intact()
    {
        var id = Guid.NewGuid();
        await Publish("webhook.received", "{\"receiptId\":\"" + id + "\"}", messageId: $"webhook.received:{id}");
        var first = await channel.BasicGetAsync("unified-inbox.worker", autoAck: false);
        first.ShouldNotBeNull();
        first!.BasicProperties.MessageId.ShouldBe($"webhook.received:{id}");
        await channel.BasicNackAsync(first.DeliveryTag, multiple: false, requeue: true);

        BasicGetResult? second = null;
        for (var attempt = 0; attempt < 20 && second is null; attempt += 1)
        {
            await Task.Delay(200);
            second = await channel.BasicGetAsync("unified-inbox.worker", autoAck: true);
        }
        second.ShouldNotBeNull();
        second!.Redelivered.ShouldBeTrue();
        second.BasicProperties.MessageId.ShouldBe($"webhook.received:{id}");
        MessageEnvelope.ExtractId(second.Body.ToArray()).ShouldBe(id);
    }

    private async Task Publish(string type, string payload, string? messageId = null)
    {
        var properties = new BasicProperties { Persistent = true, MessageId = messageId ?? Guid.NewGuid().ToString(), Type = type };
        // Publisher confirms are enabled: this completes only once the broker accepted the message.
        await channel.BasicPublishAsync("unified-inbox.events", type, mandatory: true, properties, Encoding.UTF8.GetBytes(payload));
    }

    private async Task<string> GetBody(string queue)
    {
        for (var attempt = 0; attempt < 20; attempt += 1)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true);
            if (result is not null) return Encoding.UTF8.GetString(result.Body.Span);
            await Task.Delay(200);
        }
        throw new TimeoutException($"No message arrived on {queue}.");
    }
}
