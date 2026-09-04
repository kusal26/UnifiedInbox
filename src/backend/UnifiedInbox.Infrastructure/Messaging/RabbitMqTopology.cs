using RabbitMQ.Client;

namespace UnifiedInbox.Infrastructure.Messaging;

/// <summary>
/// Single source of truth for the RabbitMQ topology shared by the API (realtime), the worker
/// (dispatcher/consumer/sweeper), and the tests.
///
/// Routing model:
///  - <see cref="EventsExchange"/> is the canonical topic exchange. The worker queue and the
///    realtime queue are bound to it; canonical event types flow to both as needed.
///  - Transient retries are scheduled by publishing a durable <see cref="RetryEnvelope"/> to
///    <see cref="RetryExchange"/>. Each retry bucket queue has a fixed TTL and dead-letters back to
///    the events exchange, where the <c>retry.*</c> binding returns the envelope to the worker queue.
///    RabbitMQ preserves the original <c>retry.&lt;bucket&gt;</c> routing key on dead-letter, so the
///    worker can recognize retried envelopes.
///  - The worker queue dead-letters poison messages (nacked with requeue=false after a redelivery)
///    to <see cref="DeadLetterRoutingKey"/>, which routes them to the terminal <see cref="DeadLetterQueue"/>.
/// </summary>
public static class RabbitMqTopology
{
    public const string EventsExchange = "unified-inbox.events";
    public const string RetryExchange = "unified-inbox.retry";
    public const string WorkerQueue = "unified-inbox.worker";
    public const string RealtimeQueue = "unified-inbox.realtime";
    public const string DeadLetterQueue = "unified-inbox.dead-letter";
    public const string DeadLetterRoutingKey = "dead-letter";
    public const string RetryPrefix = "retry.";

    /// <summary>Canonical worker event types the worker queue binds to on the events exchange.</summary>
    public static readonly string[] WorkerBindings = ["webhook.received", "outbound.message.requested"];

    /// <summary>Wildcard keys the realtime queue binds to on the events exchange.</summary>
    public static readonly string[] RealtimeBindings = ["conversation.*", "message.*", "note.*", "channel.*", "notification.*"];

    /// <summary>Durable retry buckets in the fixed schedule shared with <c>OutboxRetryPolicy</c>.</summary>
    public static readonly (string Queue, string RoutingKey, TimeSpan Ttl)[] RetryBuckets =
    [
        ("unified-inbox.retry.5s", "retry.5s", TimeSpan.FromSeconds(5)),
        ("unified-inbox.retry.30s", "retry.30s", TimeSpan.FromSeconds(30)),
        ("unified-inbox.retry.2m", "retry.2m", TimeSpan.FromMinutes(2)),
        ("unified-inbox.retry.10m", "retry.10m", TimeSpan.FromMinutes(10)),
    ];

    /// <summary>True when a delivery routing key identifies a message that came back from a retry bucket.</summary>
    public static bool IsRetryRoutingKey(string routingKey) => routingKey.StartsWith(RetryPrefix, StringComparison.Ordinal);

    /// <summary>Selects the retry bucket for a whole-entity attempt count (1 → 5s, 2 → 30s, 3 → 2m, ≥4 → 10m).</summary>
    public static string RetryRoutingKey(int attempt) => attempt switch
    {
        <= 1 => "retry.5s",
        2 => "retry.30s",
        3 => "retry.2m",
        _ => "retry.10m",
    };

    /// <summary>Declares the complete topology. Declarations are idempotent, so every process and
    /// test can call this on startup without coordination.</summary>
    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        await channel.ExchangeDeclareAsync(EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(RetryExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);

        // Worker queue: durable; poison (nacked requeue=false) dead-letters to the terminal queue.
        var workerArguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = EventsExchange,
            ["x-dead-letter-routing-key"] = DeadLetterRoutingKey,
        };
        await channel.QueueDeclareAsync(WorkerQueue, durable: true, exclusive: false, autoDelete: false, arguments: workerArguments, cancellationToken: cancellationToken);
        foreach (var key in WorkerBindings) await channel.QueueBindAsync(WorkerQueue, EventsExchange, key, cancellationToken: cancellationToken);
        // Retried envelopes come back on their preserved retry.* routing key.
        await channel.QueueBindAsync(WorkerQueue, EventsExchange, RetryPrefix + "*", cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(RealtimeQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        foreach (var key in RealtimeBindings) await channel.QueueBindAsync(RealtimeQueue, EventsExchange, key, cancellationToken: cancellationToken);

        // Terminal dead-letter queue receives poison worker deliveries.
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(DeadLetterQueue, EventsExchange, DeadLetterRoutingKey, cancellationToken: cancellationToken);

        // Retry buckets: fixed TTL, dead-letter back to the events exchange (original routing key preserved).
        foreach (var (queue, routingKey, ttl) in RetryBuckets)
        {
            var arguments = new Dictionary<string, object?>
            {
                ["x-message-ttl"] = (long)ttl.TotalMilliseconds,
                ["x-dead-letter-exchange"] = EventsExchange,
            };
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: arguments, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(queue, RetryExchange, routingKey, cancellationToken: cancellationToken);
        }
    }
}
