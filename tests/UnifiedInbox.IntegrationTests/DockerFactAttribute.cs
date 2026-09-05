namespace UnifiedInbox.IntegrationTests;

/// <summary>Runs only when Docker is available. Fails (instead of skipping) in CI so
/// PostgreSQL runtime-role, RabbitMQ retry, Redis SignalR, MinIO, and ClamAV suites are mandatory.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_DOCKER_TESTS") != "true")
        {
            if (IsMandatory())
                throw new InvalidOperationException("Docker-backed test suite is mandatory in CI: set RUN_DOCKER_TESTS=true with a running Docker daemon instead of skipping.");
            Skip = "Requires a Docker daemon (set RUN_DOCKER_TESTS=true).";
        }
    }

    internal static bool IsMandatory() =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("FAIL_ON_SKIPPED"), "true", StringComparison.OrdinalIgnoreCase);
}
