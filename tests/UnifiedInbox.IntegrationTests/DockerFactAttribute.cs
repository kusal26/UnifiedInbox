namespace UnifiedInbox.IntegrationTests;

/// <summary>Runs only when Docker is available (CI sets RUN_DOCKER_TESTS=true).</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_DOCKER_TESTS") != "true")
            Skip = "Requires a Docker daemon (set RUN_DOCKER_TESTS=true).";
    }
}
