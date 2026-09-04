using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Shouldly;
using UnifiedInbox.Infrastructure.Persistence;

namespace UnifiedInbox.Api.Tests;

/// <summary>
/// Exercises the real API host over HTTP as the <c>app_runtime</c> role: registration,
/// verification, login, authenticated reads, refresh rotation, and token-reuse revocation
/// all run through ASP.NET middleware, DI, EF Core, and forced RLS on PostgreSQL.
/// </summary>
[Collection("runtime-role")]
public sealed class RuntimeRoleAuthenticationTests(RuntimeRoleFixture fixture)
{
    [DockerFact]
    public async Task Register_verify_login_me_and_refresh_run_as_app_runtime_over_http()
    {
        var client = fixture.Factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var slug = $"rt-{suffix}";
        var email = $"owner-{suffix}@example.com";
        const string password = "supersecure-password-1";

        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { workspaceName = "Runtime", workspaceSlug = slug, displayName = "Runtime Owner", email, password });
        register.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var verification = await fixture.Mail.LastTokenAsync();
        TenantToken.TryGetTenantId(verification, out var routedTenant).ShouldBeTrue();
        routedTenant.ShouldNotBe(Guid.Empty);

        var verify = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token = verification });
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = slug, email, password });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var loginBody = await login.Content.ReadFromJsonAsync<TokenResponse>();
        loginBody!.AccessToken.ShouldNotBeNullOrWhiteSpace();
        var refreshToken = ExtractCookie(login, "refresh_token").ShouldNotBeNull();

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginBody.AccessToken);
        var me = await client.SendAsync(meRequest);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var meBody = await me.Content.ReadFromJsonAsync<MeResponse>();
        meBody!.Email.ShouldBe(email);
        meBody.WorkspaceName.ShouldBe("Runtime");

        var rotated = await RefreshAsync(client, refreshToken);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotatedBody = await rotated.Content.ReadFromJsonAsync<TokenResponse>();
        rotatedBody!.AccessToken.ShouldNotBeNullOrWhiteSpace();

        var reuse = await RefreshAsync(client, refreshToken);
        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var problem = await reuse.Content.ReadFromJsonAsync<ProblemResponse>();
        problem!.Code.ShouldBe("token_reuse_detected");
    }

    [DockerFact]
    public async Task Invalid_credentials_and_malformed_tokens_fail_closed()
    {
        var client = fixture.Factory.CreateClient();
        var badLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new { tenantSlug = "does-not-exist", email = "nobody@example.com", password = "wrong-password-1" });
        badLogin.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var badVerify = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new { token = "not-a-tenant-token" });
        badVerify.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var anonymous = await client.GetAsync("/api/v1/auth/me");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", $"refresh_token={refreshToken}");
        return await client.SendAsync(request);
    }

    private static string? ExtractCookie(HttpResponseMessage response, string name)
    {
        var cookie = response.Headers.SingleOrDefault(header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Value?
            .Select(value => value.Split(';', 2)[0])
            .FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return cookie?[(name.Length + 1)..];
    }

    private sealed record TokenResponse(string? AccessToken, DateTimeOffset? AccessTokenExpiresAt);
    private sealed record MeResponse(Guid Id, Guid TenantId, string Email, string DisplayName, string WorkspaceName);
    private sealed record ProblemResponse(string? Code, string? TraceId, string? Title, string? Detail);
}
