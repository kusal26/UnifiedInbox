using UnifiedInbox.Api.Controllers;

namespace UnifiedInbox.ArchitectureTests;

public sealed class DependencyTests
{
    [Fact]
    public void Controllers_do_not_depend_on_the_in_memory_store()
    {
        var offenders = typeof(AuthController).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(AuthController).Namespace && type.Name.EndsWith("Controller", StringComparison.Ordinal))
            .Where(type => type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Any(parameter => parameter.ParameterType.Name == "InMemoryInboxStore"))
            .Select(type => type.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Runtime_code_does_not_bypass_tenant_query_filters()
    {
        var root = FindRepositoryRoot();
        var paths = new[]
        {
            Path.Combine(root, "src", "backend", "UnifiedInbox.Api"),
            Path.Combine(root, "src", "backend", "UnifiedInbox.Worker"),
            Path.Combine(root, "src", "backend", "UnifiedInbox.Infrastructure", "Services"),
            Path.Combine(root, "src", "backend", "UnifiedInbox.Infrastructure", "Messaging")
        };
        var offenders = paths.SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            .Where(file => File.ReadAllText(file).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.Empty(offenders);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UnifiedInbox.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
