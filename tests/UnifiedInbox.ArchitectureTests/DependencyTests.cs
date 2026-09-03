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
}
