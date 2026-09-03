using System.Net;
using Shouldly;
using UnifiedInbox.Application;

namespace UnifiedInbox.Application.Tests;

public sealed class OutboxRetryPolicyTests
{
    [Fact]
    public void Retry_schedule_uses_5s_30s_2m_10m()
    {
        var random = new Random(42);
        OutboxRetryPolicy.NextDelay(1, random).TotalSeconds.ShouldBeInRange(4, 6);
        OutboxRetryPolicy.NextDelay(2, random).TotalSeconds.ShouldBeInRange(24, 36);
        OutboxRetryPolicy.NextDelay(3, random).TotalMinutes.ShouldBeInRange(1.6, 2.4);
        OutboxRetryPolicy.NextDelay(4, random).TotalMinutes.ShouldBeInRange(8, 12);
        OutboxRetryPolicy.NextDelay(99, random).TotalMinutes.ShouldBeInRange(8, 12);
    }

    [Fact]
    public void Jitter_stays_within_twenty_percent()
    {
        var random = new Random(7);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var expected = attempt switch { 1 => 5.0, 2 => 30.0, 3 => 120.0, _ => 600.0 };
            OutboxRetryPolicy.NextDelay(attempt, random).TotalSeconds.ShouldBeInRange(expected * 0.8, expected * 1.2);
        }
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void Provider_status_codes_classify_transient_vs_permanent(int status, bool transient) =>
        OutboxRetryPolicy.IsTransient(new HttpRequestException("provider", null, (HttpStatusCode)status)).ShouldBe(transient);

    [Fact]
    public void Network_and_timeout_failures_are_transient()
    {
        OutboxRetryPolicy.IsTransient(new HttpRequestException("reset")).ShouldBeTrue();
        OutboxRetryPolicy.IsTransient(new TaskCanceledException()).ShouldBeTrue();
        OutboxRetryPolicy.IsTransient(new TimeoutException()).ShouldBeTrue();
        OutboxRetryPolicy.IsTransient(new InvalidOperationException()).ShouldBeFalse();
    }

    [Fact]
    public void Only_failures_that_may_have_dispatched_are_ambiguous()
    {
        OutboxRetryPolicy.IsAmbiguous(new HttpRequestException("reset")).ShouldBeTrue();
        OutboxRetryPolicy.IsAmbiguous(new TaskCanceledException()).ShouldBeTrue();
        OutboxRetryPolicy.IsAmbiguous(new HttpRequestException("bad", null, HttpStatusCode.BadRequest)).ShouldBeFalse();
        OutboxRetryPolicy.IsAmbiguous(new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests)).ShouldBeFalse();
        OutboxRetryPolicy.IsAmbiguous(new InvalidOperationException()).ShouldBeFalse();
    }
}
