using System.Net;

namespace UnifiedInbox.Application;

/// <summary>Shared retry schedule for durable messaging: 5s / 30s / 2m / 10m with jitter.</summary>
public static class OutboxRetryPolicy
{
    public const int MaxAttempts = 5;

    public static TimeSpan NextDelay(int attempt, Random? random = null)
    {
        var baseDelay = attempt switch
        {
            <= 1 => TimeSpan.FromSeconds(5),
            2 => TimeSpan.FromSeconds(30),
            3 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(10),
        };
        // ±20% jitter so restarted workers do not retry in lockstep.
        var jitter = ((random ?? Random.Shared).NextDouble() * 0.4) - 0.2;
        return baseDelay + TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * jitter);
    }

    /// <summary>True for failures worth retrying: rate limits, 5xx, timeouts, network errors.</summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException http => http.StatusCode is null or (HttpStatusCode)429 or >= (HttpStatusCode)500,
        InboxException inbox => (inbox.Code is "provider_rate_limited" or "provider_temporarily_unavailable") || inbox.StatusCode >= 500,
        TaskCanceledException => true,
        TimeoutException => true,
        _ => false,
    };

    /// <summary>
    /// True when the operation may have reached the provider before failing (timeouts,
    /// dropped connections). Ambiguous sends must be reconciled, never blindly resent.
    /// </summary>
    public static bool IsAmbiguous(Exception exception) => exception switch
    {
        HttpRequestException http => http.StatusCode is null,
        TaskCanceledException => true,
        TimeoutException => true,
        _ => false,
    };
}

/// <summary>Structured realtime envelope delivered over SignalR instead of opaque strings.</summary>
public sealed record RealtimeEvent(string Type, object? Data, DateTimeOffset OccurredAt);
