using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Api.Tests;

/// <summary>
/// Proves authorization denials are both returned as stable RFC 7807 problems (code + trace id)
/// and written to the audit log with tenant, actor, method, policy, and normalized route — without
/// leaking request bodies or secrets. Runs over the real API host as <c>app_runtime</c>.
/// </summary>
[Collection("runtime-role")]
public sealed class AuthorizationAuditTests(RuntimeRoleFixture fixture)
{
    [DockerFact]
    public async Task Policy_denials_are_audited_and_return_a_stable_forbidden_problem()
    {
        var (client, tenantId, _, agentId, ownerToken, agentToken) = await SeedAsync();
        using var allow = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/connect/attempt");
        allow.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken); // sanity: an Owner passes
        allow.Content = JsonContent.Create(new { displayName = "Sales" });
        (await client.SendAsync(allow)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var denied = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/connect/attempt");
        denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);
        denied.Content = JsonContent.Create(new { displayName = "Sales" });
        var response = await client.SendAsync(denied);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        problem!.Code.ShouldBe("forbidden");
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            var audit = await db.AuditEntries.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.Action == "authorization.denied").ToListAsync();
            var entry = audit.ShouldHaveSingleItem();
            entry.ActorId.ShouldBe(agentId);
            entry.Resource.ShouldContain("connect/attempt");
            entry.Metadata.ShouldContain("\"method\":\"POST\"");
            entry.Metadata.ShouldContain("\"policy\":\"Admin\"");
        }
    }

    [DockerFact]
    public async Task Deactivated_members_are_denied_and_the_failure_is_audited()
    {
        var (client, tenantId, ownerId, _, ownerToken, _) = await SeedAsync();

        // Active owner passes the Admin policy and the membership re-read.
        using var before = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/connect/attempt");
        before.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        before.Content = JsonContent.Create(new { displayName = "Sales" });
        (await client.SendAsync(before)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            var owner = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == ownerId);
            owner.IsActive = false;
            await db.SaveChangesAsync();
        }

        // The owner's JWT still carries an Owner role claim, so the policy passes at the middleware
        // and the membership re-read (service layer) is what rejects the call.
        using var denied = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/connect/attempt");
        denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        denied.Content = JsonContent.Create(new { displayName = "Sales" });
        var response = await client.SendAsync(denied);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        problem!.Code.ShouldBe("forbidden");
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            var audit = await db.AuditEntries.IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.Action == "authorization.denied" && x.ActorId == ownerId).ToListAsync();
            var entry = audit.ShouldHaveSingleItem();
            entry.Resource.ShouldContain("connect/attempt");
            entry.Metadata.ShouldContain("\"policy\":\"Admin\"");
        }
    }

    /// <summary>Creates a tenant with an Owner and an Agent (both verified/active) and returns bearer
    /// tokens for each. Password hashing uses the API host's own <c>IPasswordHasher</c>.</summary>
    private async Task<(HttpClient Client, Guid TenantId, Guid OwnerId, Guid AgentId, string OwnerToken, string? AgentToken)> SeedAsync()
    {
        var client = fixture.Factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tenantId = Guid.NewGuid();
        var slug = $"audit-{suffix}";
        var ownerEmail = $"owner-{suffix}@example.com";
        var agentEmail = $"agent-{suffix}@example.com";
        const string password = "supersecure-password-1";

        Guid ownerId;
        Guid agentId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
            await using var db = fixture.Context(fixture.OwnerConnection);
            db.Tenants.Add(new Tenant(tenantId, slug, "Audit Workspace"));
            var owner = new User(Guid.NewGuid(), tenantId, ownerEmail, "Owner", UserRole.Owner) { NormalizedEmail = ownerEmail.ToUpperInvariant(), EmailVerifiedAt = DateTimeOffset.UtcNow };
            owner.PasswordHash = hasher.HashPassword(owner, password);
            var agent = new User(Guid.NewGuid(), tenantId, agentEmail, "Agent", UserRole.Agent) { NormalizedEmail = agentEmail.ToUpperInvariant(), EmailVerifiedAt = DateTimeOffset.UtcNow };
            agent.PasswordHash = hasher.HashPassword(agent, password);
            db.Users.AddRange(owner, agent);
            await db.SaveChangesAsync();
            ownerId = owner.Id;
            agentId = agent.Id;
        }

        var ownerToken = await LoginAsync(client, slug, ownerEmail, password);
        var agentToken = await LoginAsync(client, slug, agentEmail, password);
        return (client, tenantId, ownerId, agentId, ownerToken, agentToken);
    }

    private async Task<string> LoginAsync(HttpClient client, string slug, string email, string password)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<TokenResponse>();
        return body!.AccessToken!;
    }

    private sealed record TokenResponse(string? AccessToken, DateTimeOffset? AccessTokenExpiresAt);
    private sealed record ProblemResponse(string? Code, string? TraceId, string? Title, string? Detail);
}
