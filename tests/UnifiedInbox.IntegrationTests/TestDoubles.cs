using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.IntegrationTests;

internal sealed class TestTenant(Guid tenantId, Guid userId, UserRole role = UserRole.Owner) : ICurrentTenant
{
    public Guid? TenantId => tenantId;
    public Guid? UserId => userId;
    public UserRole? Role => role;
}

internal sealed class TestEnvironment(string environmentName = "Test") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "tests";
    public string ContentRootPath { get; set; } = "/";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class FakeMailSender : IMailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];
    public Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken)
    {
        Sent.Add((to, subject, textBody));
        return Task.CompletedTask;
    }

    public string LastToken() => Sent[^1].Body.Split("token: ").Last().Trim();
}

internal sealed class FakeTokenIssuer : ITokenIssuer
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(User user) => ("access-" + user.Id.ToString("N"), DateTimeOffset.UtcNow.AddMinutes(15));
}

internal static class TestContexts
{    public static (InboxDbContext Db, TestTenant Tenant) Create(Guid tenantId, Guid userId, UserRole role = UserRole.Owner, string? dbName = null)
    {
        var tenant = new TestTenant(tenantId, userId, role);
        var options = new DbContextOptionsBuilder<InboxDbContext>().UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString()).Options;
        return (new InboxDbContext(options, tenant), tenant);
    }

    public static User SeedUser(InboxDbContext db, Guid tenantId, Guid userId, UserRole role, string email = "member@example.com")
    {
        var user = new User(userId, tenantId, email, "Member", role)
        {
            NormalizedEmail = email.ToUpperInvariant(),
            EmailVerifiedAt = DateTimeOffset.UtcNow,
        };
        // Same-store seeding: bypass the fail-closed filter via the shared InMemory database.
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    public static Tenant SeedTenant(InboxDbContext db, Guid tenantId, string slug = "acme")
    {
        var tenant = new Tenant(tenantId, slug, "Acme");
        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant;
    }
}

internal sealed class DictionaryConfiguration(Dictionary<string, string?> values) : IConfiguration
{
    public string? this[string key] { get => values.TryGetValue(key, out var value) ? value : null; set => values[key] = value; }
    public IEnumerable<IConfigurationSection> GetChildren() => [];
    public IChangeToken GetReloadToken() => new CancellationChangeToken(new CancellationToken());
    public IConfigurationSection GetSection(string key) => new Section(values, key);

    private sealed class Section(Dictionary<string, string?> values, string path) : IConfigurationSection
    {
        public string? this[string key] { get => values.TryGetValue(path + ":" + key, out var value) ? value : null; set => values[path + ":" + key] = value; }
        public string Key => path.Split(':').Last();
        public string Path => path;
        public string? Value { get => values.TryGetValue(path, out var value) ? value : null; set => values[path] = value; }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => new CancellationChangeToken(new CancellationToken());
        public IConfigurationSection GetSection(string key) => new Section(values, path + ":" + key);
    }
}
