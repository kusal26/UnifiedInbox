namespace UnifiedInbox.Api.Tests;

/// <summary>Docker-backed API test. Fails (instead of skipping) in CI so runtime-role,
/// retry, realtime, MinIO, and ClamAV suites are mandatory.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_DOCKER_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
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
