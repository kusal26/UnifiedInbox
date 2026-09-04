namespace UnifiedInbox.Api.Tests;

public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_DOCKER_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
            Skip = "Requires a Docker daemon (set RUN_DOCKER_TESTS=true).";
    }
}
