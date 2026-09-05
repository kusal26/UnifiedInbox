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
/// Authorization matrix for the channels surface: Admin/Owner policies, membership re-reads,
/// cross-tenant 404s, connect/attempt validation and secret hygiene. Runs over the real API host as
/// <c>app_runtime</c>. WhatsApp channels are seeded via the owner role; no Graph/MinIO calls occur
/// because test channels carry no credential or business id.
/// </summary>
[Collection("runtime-role")]
public sealed class ChannelApiTests(RuntimeRoleFixture fixture)
{
    private const string Password = "supersecure-password-1";

    [DockerFact]
    public async Task All_channel_routes_require_authentication()
    {
        using var client = fixture.Factory.CreateClient();
        var id = Guid.NewGuid();
        var anonymous = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Post, "/api/v1/channels/connect/attempt"),
            (HttpMethod.Post, $"/api/v1/channels/{id}/test"),
            (HttpMethod.Get, $"/api/v1/channels/{id}/health"),
            (HttpMethod.Put, $"/api/v1/channels/{id}/enabled"),
            (HttpMethod.Post, $"/api/v1/channels/{id}/disconnect"),
            (HttpMethod.Post, "/api/v1/channels/credentials/rotate"),
            (HttpMethod.Get, $"/api/v1/channels/{id}/templates"),
            (HttpMethod.Post, $"/api/v1/channels/{id}/reauthorize"),
        };
        foreach (var (method, url) in anonymous)
        {
            using var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post && url.EndsWith("/connect/attempt")) request.Content = JsonContent.Create(new { displayName = "Sales" });
            if (method == HttpMethod.Put) request.Content = JsonContent.Create(new { enabled = true });
            (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [DockerFact]
    public async Task Agents_are_denied_admin_policy_endpoints_with_a_stable_forbidden_problem()
    {
        var seed = await SeedAsync();
        var endpoints = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Put, $"/api/v1/channels/{seed.ChannelId}/enabled"),
            (HttpMethod.Get, $"/api/v1/channels/{seed.ChannelId}/health"),
            (HttpMethod.Post, $"/api/v1/channels/{seed.ChannelId}/test"),
            (HttpMethod.Post, $"/api/v1/channels/{seed.ChannelId}/reauthorize"),
            (HttpMethod.Post, $"/api/v1/channels/{seed.ChannelId}/disconnect"),
        };
        foreach (var (method, url) in endpoints)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AgentToken);
            if (method == HttpMethod.Put) request.Content = JsonContent.Create(new { enabled = true });
            var response = await seed.Client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var problem = await ReadProblemAsync(response);
            problem.Code.ShouldBe("forbidden");
            problem.TraceId.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [DockerFact]
    public async Task Owner_only_credential_rotation_rejects_admin_and_agent()
    {
        var seed = await SeedAsync();
        using (var adminRotate = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/credentials/rotate"))
        {
            adminRotate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            var response = await seed.Client.SendAsync(adminRotate);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ReadProblemAsync(response)).Code.ShouldBe("forbidden");
        }
        using (var agentRotate = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/credentials/rotate"))
        {
            agentRotate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AgentToken);
            (await seed.Client.SendAsync(agentRotate)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        using (var ownerRotate = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/credentials/rotate"))
        {
            ownerRotate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
            var response = await seed.Client.SendAsync(ownerRotate);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<RotateResponse>();
            body!.Rotated.ShouldBe(0);
        }
        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            var audit = await db.AuditEntries.IgnoreQueryFilters()
                .Where(x => x.TenantId == seed.TenantId && x.Action == "credentials.rotated").ToListAsync();
            var entry = audit.ShouldHaveSingleItem();
            entry.ActorId.ShouldBe(seed.OwnerId);
        }
    }

    [DockerFact]
    public async Task Admin_runs_channel_lifecycle_health_and_templates()
    {
        var seed = await SeedAsync();
        using var healthRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/channels/{seed.ChannelId}/health");
        healthRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
        var health = await seed.Client.SendAsync(healthRequest);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        var history = await health.Content.ReadFromJsonAsync<List<HealthResponse>>();
        history!.Select(x => x.Reason).ShouldContain("connected");

        using var enableRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/channels/{seed.ChannelId}/enabled");
        enableRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
        enableRequest.Content = JsonContent.Create(new { enabled = false });
        var disabled = await seed.Client.SendAsync(enableRequest);
        disabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await disabled.Content.ReadFromJsonAsync<ChannelSummaryResponse>())!.IsEnabled.ShouldBeFalse();

        using var testRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/channels/{seed.ChannelId}/test");
        testRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
        var test = await seed.Client.SendAsync(testRequest);
        test.StatusCode.ShouldBe(HttpStatusCode.OK);
        var testBody = await test.Content.ReadFromJsonAsync<TestResultResponse>();
        testBody!.Healthy.ShouldBeFalse();
        testBody.Detail.ShouldContain("No credential");

        // A channel without a business id/credential surfaces no templates (and never calls Graph).
        using var templatesRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/channels/{seed.ChannelId}/templates");
        templatesRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
        var templates = await seed.Client.SendAsync(templatesRequest);
        templates.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await templates.Content.ReadAsStringAsync()).ShouldBe("[]");

        using var disconnectRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/channels/{seed.ChannelId}/disconnect");
        disconnectRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
        (await seed.Client.SendAsync(disconnectRequest)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            var channel = await db.Channels.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.ChannelId);
            channel.IsEnabled.ShouldBeFalse();
            channel.Status.ShouldBe("disconnected");
        }
    }

    [DockerFact]
    public async Task Missing_and_cross_tenant_channels_return_stable_404_problems()
    {
        var seed = await SeedAsync();
        var randomId = Guid.NewGuid();
        var requests = new (string Token, HttpMethod Method, string Url)[]
        {
            (seed.OwnerToken, HttpMethod.Get, $"/api/v1/channels/{randomId}/health"),
            (seed.OwnerToken, HttpMethod.Get, $"/api/v1/channels/{randomId}/templates"),
            (seed.ForeignOwnerToken, HttpMethod.Get, $"/api/v1/channels/{seed.ChannelId}/health"),
            (seed.ForeignOwnerToken, HttpMethod.Get, $"/api/v1/channels/{seed.ChannelId}/templates"),
            (seed.ForeignOwnerToken, HttpMethod.Put, $"/api/v1/channels/{seed.ChannelId}/enabled"),
            (seed.ForeignOwnerToken, HttpMethod.Post, $"/api/v1/channels/{seed.ChannelId}/reauthorize"),
        };
        foreach (var (token, method, url) in requests)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (method == HttpMethod.Put) request.Content = JsonContent.Create(new { enabled = true });
            var response = await seed.Client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            var problem = await ReadProblemAsync(response);
            problem.Code.ShouldBe("channel_not_found");
            problem.TraceId.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [DockerFact]
    public async Task Connect_attempt_validates_input_and_never_exposes_secrets()
    {
        var seed = await SeedAsync();
        using (var emptyName = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/connect/attempt"))
        {
            emptyName.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
            emptyName.Content = JsonContent.Create(new { displayName = "" });
            var response = await seed.Client.SendAsync(emptyName);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReadProblemAsync(response)).Code.ShouldBe("invalid_request");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/connect/attempt");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
        request.Content = JsonContent.Create(new { displayName = "Sales" });
        var ok = await seed.Client.SendAsync(request);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await ok.Content.ReadAsStringAsync();
        // Only the documented public handshake shape is returned; nothing secret-shaped may leak.
        raw.ShouldNotContain("accessToken", Case.Insensitive);
        raw.ShouldNotContain("access_token", Case.Insensitive);
        raw.ShouldNotContain("appSecret", Case.Insensitive);
        raw.ShouldNotContain("secret", Case.Insensitive);
        raw.ShouldNotContain("encrypted", Case.Insensitive);
        var attempt = System.Text.Json.JsonSerializer.Deserialize<ConnectionAttemptResponse>(raw, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        attempt!.AttemptId.ShouldNotBe(Guid.Empty);
        attempt.State.ShouldNotBeNullOrWhiteSpace();
        attempt.Nonce.ShouldNotBeNullOrWhiteSpace();
        attempt.ExpiresAt.ShouldNotBe(default);
    }

    [DockerFact]
    public async Task Deactivated_members_are_rejected_by_the_membership_re_read_on_templates()
    {
        var seed = await SeedAsync();
        using (var before = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/channels/{seed.ChannelId}/templates"))
        {
            before.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
            (await seed.Client.SendAsync(before)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            var owner = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.OwnerId);
            owner.IsActive = false;
            await db.SaveChangesAsync();
        }

        // The JWT still claims Owner, but the service re-reads membership and rejects the call.
        using var after = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/channels/{seed.ChannelId}/templates");
        after.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
        var response = await seed.Client.SendAsync(after);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("forbidden");
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
    }

    private async Task<SeedData> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tenantA = new Tenant(Guid.NewGuid(), $"chan-{suffix}", "Channels A");
        var tenantB = new Tenant(Guid.NewGuid(), $"chanf-{suffix}", "Channels B");
        var owner = NewUser(tenantA.Id, $"owner-{suffix}@example.com", "Owner", UserRole.Owner);
        var admin = NewUser(tenantA.Id, $"admin-{suffix}@example.com", "Admin", UserRole.Admin);
        var agent = NewUser(tenantA.Id, $"agent-{suffix}@example.com", "Agent", UserRole.Agent);
        var foreignOwner = NewUser(tenantB.Id, $"foreign-{suffix}@example.com", "Foreign", UserRole.Owner);
        var channelId = Guid.NewGuid();

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            db.Tenants.AddRange(tenantA, tenantB);
            db.Users.AddRange(owner, admin, agent, foreignOwner);
            db.Channels.Add(new Channel(channelId, tenantA.Id, "whatsapp", $"1555{suffix}", true) { DisplayName = "Sales Line", IsEnabled = true, Status = "connected" });
            db.ChannelHealth.Add(new ChannelHealth { TenantId = tenantA.Id, ChannelId = channelId, IsHealthy = true, Reason = "connected" });
            await db.SaveChangesAsync();
        }

        var client = fixture.Factory.CreateClient();
        var ownerToken = await LoginAsync(client, tenantA.Slug, owner.Email);
        var adminToken = await LoginAsync(client, tenantA.Slug, admin.Email);
        var agentToken = await LoginAsync(client, tenantA.Slug, agent.Email);
        var foreignOwnerToken = await LoginAsync(client, tenantB.Slug, foreignOwner.Email);
        return new SeedData(client, tenantA.Id, owner.Id, channelId, ownerToken, adminToken, agentToken, foreignOwnerToken);
    }

    private User NewUser(Guid tenantId, string email, string displayName, UserRole role)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var user = new User(Guid.NewGuid(), tenantId, email, displayName, role)
        {
            NormalizedEmail = email.ToUpperInvariant(),
            EmailVerifiedAt = DateTimeOffset.UtcNow,
        };
        user.PasswordHash = hasher.HashPassword(user, Password);
        return user;
    }

    private async Task<string> LoginAsync(HttpClient client, string slug, string email)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password = Password });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<TokenResponse>();
        return body!.AccessToken!;
    }

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        return (await response.Content.ReadFromJsonAsync<ProblemResponse>())!;
    }

    private sealed record SeedData(HttpClient Client, Guid TenantId, Guid OwnerId, Guid ChannelId, string OwnerToken, string AdminToken, string AgentToken, string ForeignOwnerToken);
    private sealed record TokenResponse(string? AccessToken, DateTimeOffset? AccessTokenExpiresAt);
    private sealed record RotateResponse(int Rotated);
    private sealed record HealthResponse(string Reason);
    private sealed record ChannelSummaryResponse(bool IsEnabled, string Status);
    private sealed record TestResultResponse(bool Healthy, string Detail);
    private sealed record ConnectionAttemptResponse(Guid AttemptId, string State, string Nonce, string MetaAppId, string ConfigurationId, string GraphVersion, string EmbeddedSignupVersion, DateTimeOffset ExpiresAt);
    private sealed record ProblemResponse(string? Code, string? TraceId, string? Title, string? Detail);
}
