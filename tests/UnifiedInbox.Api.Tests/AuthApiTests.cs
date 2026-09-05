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
/// Authorization matrix for the auth surface the RuntimeRoleAuthenticationTests do not cover:
/// <c>me</c>, session listing/revocation, and the anti-enumeration flows (resend-verification,
/// forgot-password). Runs over the real API host as <c>app_runtime</c>.
/// </summary>
[Collection("runtime-role")]
public sealed class AuthApiTests(RuntimeRoleFixture fixture)
{
    private const string Password = "supersecure-password-1";

    [DockerFact]
    public async Task Me_sessions_and_session_revocation_require_authentication()
    {
        using var client = fixture.Factory.CreateClient();
        (await client.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/v1/auth/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.DeleteAsync($"/api/v1/auth/sessions/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.DeleteAsync("/api/v1/auth/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task Verified_agent_reads_its_own_me_and_revokes_a_session()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var slug = $"auth-{suffix}";
        var email = $"agent-{suffix}@example.com";
        var tenantId = await SeedTenantAsync(slug, "Auth Me");
        var agentId = await SeedUserAsync(tenantId, email, "Agent A", UserRole.Agent, verified: true);
        using var client = fixture.Factory.CreateClient();
        var token = await LoginAsync(client, slug, email);

        using (var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me"))
        {
            meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var me = await client.SendAsync(meRequest);
            me.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await me.Content.ReadFromJsonAsync<MeResponse>();
            body!.Email.ShouldBe(email);
            body.TenantId.ShouldBe(tenantId);
        }

        Guid sessionId;
        using (var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/sessions"))
        {
            listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var sessions = await client.SendAsync(listRequest);
            sessions.StatusCode.ShouldBe(HttpStatusCode.OK);
            var items = await sessions.Content.ReadFromJsonAsync<List<SessionResponse>>();
            items!.ShouldHaveSingleItem();
            sessionId = items[0].Id;
        }

        using (var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/auth/sessions/{sessionId}"))
        {
            revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await client.SendAsync(revoke)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            var session = await db.RefreshTokens.IgnoreQueryFilters().SingleAsync(x => x.Id == sessionId);
            session.UserId.ShouldBe(agentId);
            session.RevokedAt.ShouldNotBeNull();
        }
    }

    [DockerFact]
    public async Task Revoking_all_sessions_revokes_every_refresh_token_for_the_caller()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var slug = $"authall-{suffix}";
        var email = $"agent-{suffix}@example.com";
        var tenantId = await SeedTenantAsync(slug, "Auth All");
        var agentId = await SeedUserAsync(tenantId, email, "Agent All", UserRole.Agent, verified: true);
        using var client = fixture.Factory.CreateClient();
        var token = await LoginAsync(client, slug, email);

        using (var revoke = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/auth/sessions"))
        {
            revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await client.SendAsync(revoke)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            var sessions = await db.RefreshTokens.IgnoreQueryFilters().Where(x => x.UserId == agentId).ToListAsync();
            sessions.ShouldNotBeEmpty();
            sessions.ShouldAllBe(x => x.RevokedAt != null);
        }
    }

    [DockerFact]
    public async Task Sessions_are_tenant_scoped_and_cross_tenant_revocation_is_a_noop()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var slugA = $"autha-{suffix}";
        var slugB = $"authb-{suffix}";
        var emailA = $"agent-a-{suffix}@example.com";
        var emailB = $"agent-b-{suffix}@example.com";
        var tenantA = await SeedTenantAsync(slugA, "Auth A");
        var tenantB = await SeedTenantAsync(slugB, "Auth B");
        await SeedUserAsync(tenantA, emailA, "Agent A", UserRole.Agent, verified: true);
        await SeedUserAsync(tenantB, emailB, "Agent B", UserRole.Agent, verified: true);
        using var client = fixture.Factory.CreateClient();
        var tokenA = await LoginAsync(client, slugA, emailA);
        var tokenB = await LoginAsync(client, slugB, emailB);

        var aSessions = await GetSessionsAsync(client, tokenA);
        var bSessions = await GetSessionsAsync(client, tokenB);
        aSessions.ShouldHaveSingleItem();
        bSessions.ShouldHaveSingleItem();
        var aSessionId = aSessions[0].Id;
        var bSessionId = bSessions[0].Id;

        // Neither tenant can see the other tenant's sessions.
        aSessions.Select(x => x.Id).ShouldNotContain(bSessionId);
        bSessions.Select(x => x.Id).ShouldNotContain(aSessionId);

        // Tenant B revoking tenant A's session id is a silent no-op: no cross-tenant write happens.
        using (var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/auth/sessions/{aSessionId}"))
        {
            revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
            (await client.SendAsync(revoke)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            (await db.RefreshTokens.IgnoreQueryFilters().SingleAsync(x => x.Id == aSessionId)).RevokedAt.ShouldBeNull();
        }
    }

    [DockerFact]
    public async Task Malformed_and_unknown_session_ids_fail_closed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var slug = $"authbad-{suffix}";
        var email = $"agent-{suffix}@example.com";
        var tenantId = await SeedTenantAsync(slug, "Auth Bad");
        await SeedUserAsync(tenantId, email, "Agent Bad", UserRole.Agent, verified: true);
        using var client = fixture.Factory.CreateClient();
        var token = await LoginAsync(client, slug, email);

        using (var malformed = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/auth/sessions/not-a-guid"))
        {
            malformed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await client.SendAsync(malformed)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // An unknown (but well-formed) session id must not error and must not touch other rows.
        using var unknown = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/auth/sessions/{Guid.NewGuid()}");
        unknown.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.SendAsync(unknown)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [DockerFact]
    public async Task Resend_verification_and_forgot_password_do_not_enumerate_accounts()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tenantId = await SeedTenantAsync($"authne-{suffix}", "Auth AntiEnum");
        var verifiedEmail = $"verified-{suffix}@example.com";
        var unverifiedEmail = $"unverified-{suffix}@example.com";
        await SeedUserAsync(tenantId, verifiedEmail, "Verified", UserRole.Owner, verified: true);
        await SeedUserAsync(tenantId, unverifiedEmail, "Unverified", UserRole.Agent, verified: false);

        using var client = fixture.Factory.CreateClient();

        // resend-verification for an existing (verified) account is indistinguishable from a missing one.
        var resendExisting = await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new { email = verifiedEmail });
        resendExisting.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var resendMissing = await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new { email = $"missing-{suffix}@example.com" });
        resendMissing.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await resendExisting.Content.ReadAsStringAsync()).ShouldBe(await resendMissing.Content.ReadAsStringAsync());

        // forgot-password for an existing but unverified account is likewise indistinguishable.
        var forgotExisting = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = unverifiedEmail });
        forgotExisting.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var forgotMissing = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = $"missing-{suffix}-2@example.com" });
        forgotMissing.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await forgotExisting.Content.ReadAsStringAsync()).ShouldBe(await forgotMissing.Content.ReadAsStringAsync());
    }

    private async Task<List<SessionResponse>> GetSessionsAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/sessions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<SessionResponse>>())!;
    }

    private async Task<Guid> SeedTenantAsync(string slug, string name)
    {
        var tenant = new Tenant(Guid.NewGuid(), slug, name);
        await using var db = fixture.Context(fixture.OwnerConnection);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> SeedUserAsync(Guid tenantId, string email, string displayName, UserRole role, bool verified)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var user = new User(Guid.NewGuid(), tenantId, email, displayName, role)
        {
            NormalizedEmail = email.ToUpperInvariant(),
            EmailVerifiedAt = verified ? DateTimeOffset.UtcNow : null,
        };
        user.PasswordHash = hasher.HashPassword(user, Password);
        await using var db = fixture.Context(fixture.OwnerConnection);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<string> LoginAsync(HttpClient client, string slug, string email)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password = Password });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<TokenResponse>();
        return body!.AccessToken!;
    }

    private sealed record TokenResponse(string? AccessToken, DateTimeOffset? AccessTokenExpiresAt);
    private sealed record MeResponse(Guid Id, Guid TenantId, string Email, string DisplayName, string WorkspaceName);
    private sealed record SessionResponse(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool IsCurrent);
}
