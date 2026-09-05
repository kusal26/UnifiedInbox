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
/// Authorization and state matrix for the attachments surface: staging validation, cross-tenant
/// invisibility, and download/complete state transitions. Runs over the real API host as
/// <c>app_runtime</c>. No MinIO is configured in this fixture, so rows are seeded through the owner
/// role and assertions avoid depending on real object bytes (the presigned download URL is generated
/// locally, while <c>complete</c> of a staging-only record is asserted to fail with a stable problem).
/// </summary>
[Collection("runtime-role")]
public sealed class AttachmentApiTests(RuntimeRoleFixture fixture)
{
    private const string Password = "supersecure-password-1";

    [DockerFact]
    public async Task All_attachment_routes_require_authentication()
    {
        using var client = fixture.Factory.CreateClient();
        using var stage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/attachments/staging");
        stage.Content = JsonContent.Create(new { fileName = "a.pdf", contentType = "application/pdf", size = 4 });
        (await client.SendAsync(stage)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var legacy = new HttpRequestMessage(HttpMethod.Post, "/api/v1/attachments");
        legacy.Content = JsonContent.Create(new { fileName = "a.pdf", contentType = "application/pdf", size = 4 });
        (await client.SendAsync(legacy)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using (var complete = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/attachments/{Guid.NewGuid()}/complete"))
        (await client.SendAsync(complete)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using (var download = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/attachments/{Guid.NewGuid()}/download"))
        (await client.SendAsync(download)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task Staging_rejects_malformed_upload_metadata_with_stable_problems()
    {
        var (client, token) = await SeedTokenAsync("attachbad");
        using var badSize = Authorized(token, HttpMethod.Post, "/api/v1/attachments/staging");
        badSize.Content = JsonContent.Create(new { fileName = "a.pdf", contentType = "application/pdf", size = 0 });
        var sizeResponse = await client.SendAsync(badSize);
        sizeResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var sizeProblem = await ReadProblemAsync(sizeResponse);
        sizeProblem.Code.ShouldBe("invalid_request");
        sizeProblem.TraceId.ShouldNotBeNullOrWhiteSpace();

        using var badType = Authorized(token, HttpMethod.Post, "/api/v1/attachments/staging");
        badType.Content = JsonContent.Create(new { fileName = "a.pdf", contentType = "text/plain", size = 4 });
        var typeResponse = await client.SendAsync(badType);
        typeResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(typeResponse)).Code.ShouldBe("invalid_request");

        using var noName = Authorized(token, HttpMethod.Post, "/api/v1/attachments/staging");
        noName.Content = JsonContent.Create(new { fileName = "", contentType = "application/pdf", size = 4 });
        var nameResponse = await client.SendAsync(noName);
        nameResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(nameResponse)).Code.ShouldBe("invalid_request");

        // Extension/content-type mismatch is a distinct, stable 400 problem code.
        using var mismatch = Authorized(token, HttpMethod.Post, "/api/v1/attachments/staging");
        mismatch.Content = JsonContent.Create(new { fileName = "notes.txt", contentType = "application/pdf", size = 4 });
        var mismatchResponse = await client.SendAsync(mismatch);
        mismatchResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(mismatchResponse)).Code.ShouldBe("malicious_attachment");

        // Unparseable JSON body is the [ApiController] 400 invalid_request path.
        using var malformed = Authorized(token, HttpMethod.Post, "/api/v1/attachments/staging");
        malformed.Content = new StringContent("{ \"fileName\": ", System.Text.Encoding.UTF8, "application/json");
        var malformedResponse = await client.SendAsync(malformed);
        malformedResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await malformedResponse.Content.ReadAsStringAsync()).ShouldContain("\"code\":\"invalid_request\"");
    }

    [DockerFact]
    public async Task Cross_tenant_attachments_are_invisible()
    {
        var seed = await SeedTwoTenantsAsync();
        // A Ready attachment owned by tenant A must be invisible to tenant B on both download and complete.
        using (var download = Authorized(seed.TokenB, HttpMethod.Get, $"/api/v1/attachments/{seed.AttachmentAReady}/download"))
        {
            var response = await seed.Client.SendAsync(download);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        using (var complete = Authorized(seed.TokenB, HttpMethod.Post, $"/api/v1/attachments/{seed.AttachmentAReady}/complete"))
        {
            var response = await seed.Client.SendAsync(complete);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        // A random id is equally invisible to the owner of the staging tenant.
        using (var random = Authorized(seed.TokenA, HttpMethod.Get, $"/api/v1/attachments/{Guid.NewGuid()}/download"))
        (await seed.Client.SendAsync(random)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Download_and_complete_reflect_attachment_state_for_the_owner_tenant()
    {
        var seed = await SeedTwoTenantsAsync();
        var (client, tokenA) = (seed.Client, seed.TokenA);

        // Ready + unexpired is downloadable; the presigned URL is generated locally (no MinIO call).
        using (var download = Authorized(tokenA, HttpMethod.Get, $"/api/v1/attachments/{seed.AttachmentAReady}/download"))
        {
            var response = await client.SendAsync(download);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<DownloadResponse>();
            body!.DownloadUrl.ShouldNotBeNullOrWhiteSpace();
            body.FileName.ShouldBe("report-a.pdf");
            body.ContentType.ShouldBe("application/pdf");
        }

        // A staging-only record is never downloadable.
        using (var staged = Authorized(tokenA, HttpMethod.Get, $"/api/v1/attachments/{seed.AttachmentAStaged}/download"))
        (await client.SendAsync(staged)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // An expired record is never downloadable.
        using (var expired = Authorized(tokenA, HttpMethod.Get, $"/api/v1/attachments/{seed.AttachmentAExpired}/download"))
        (await client.SendAsync(expired)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Completing an already-complete (Ready) record is a stable 409.
        using (var completeReady = Authorized(tokenA, HttpMethod.Post, $"/api/v1/attachments/{seed.AttachmentAReady}/complete"))
        {
            var response = await client.SendAsync(completeReady);
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var problem = await ReadProblemAsync(response);
            problem.Code.ShouldBe("attachment_already_claimed");
            problem.TraceId.ShouldNotBeNullOrWhiteSpace();
        }

        // Completing an expired staging record is a stable 410.
        using (var completeExpired = Authorized(tokenA, HttpMethod.Post, $"/api/v1/attachments/{seed.AttachmentAExpired}/complete"))
        {
            var response = await client.SendAsync(completeExpired);
            response.StatusCode.ShouldBe(HttpStatusCode.Gone);
            (await ReadProblemAsync(response)).Code.ShouldBe("attachment_expired");
        }

        // Completing a fresh staging record whose bytes were never uploaded must not succeed or leak
        // a stack trace: it is either a stable "upload incomplete" 422 (object store reachable) or a
        // sanitized 500 problem (store unreachable, as in this fixture).
        using var completeStaged = Authorized(tokenA, HttpMethod.Post, $"/api/v1/attachments/{seed.AttachmentAStaged}/complete");
        var stagedResponse = await client.SendAsync(completeStaged);
        if (stagedResponse.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            (await ReadProblemAsync(stagedResponse)).Code.ShouldBe("attachment_upload_incomplete");
        }
        else
        {
            stagedResponse.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            var problem = await ReadProblemAsync(stagedResponse);
            problem.Code.ShouldBe("internal_error");
            problem.TraceId.ShouldNotBeNullOrWhiteSpace();
            var body = await stagedResponse.Content.ReadAsStringAsync();
            body.ShouldNotContain("at UnifiedInbox", Case.Insensitive);
        }
    }

    private async Task<(HttpClient Client, string Token)> SeedTokenAsync(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var slug = $"{prefix}-{suffix}";
        var tenant = new Tenant(Guid.NewGuid(), slug, "Attachment");
        var user = NewUser(tenant.Id, $"owner-{suffix}@example.com", "Owner", UserRole.Owner);
        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            db.Tenants.Add(tenant);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        var client = fixture.Factory.CreateClient();
        return (client, await LoginAsync(client, slug, user.Email));
    }

    private async Task<SeedData> SeedTwoTenantsAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tenantA = new Tenant(Guid.NewGuid(), $"atta-{suffix}", "Attachment A");
        var tenantB = new Tenant(Guid.NewGuid(), $"attb-{suffix}", "Attachment B");
        var ownerA = NewUser(tenantA.Id, $"owner-a-{suffix}@example.com", "Owner A", UserRole.Owner);
        var ownerB = NewUser(tenantB.Id, $"owner-b-{suffix}@example.com", "Owner B", UserRole.Owner);

        var ready = Guid.NewGuid();
        var staged = Guid.NewGuid();
        var expired = Guid.NewGuid();
        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            db.Tenants.AddRange(tenantA, tenantB);
            db.Users.AddRange(ownerA, ownerB);
            db.Attachments.AddRange(
                new Attachment { Id = ready, TenantId = tenantA.Id, UploaderId = ownerA.Id, ObjectKey = $"{tenantA.Id:N}/{ready:N}/report-a.pdf", FileName = "report-a.pdf", ContentType = "application/pdf", Size = 4, Status = AttachmentStatus.Ready, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) },
                new Attachment { Id = staged, TenantId = tenantA.Id, UploaderId = ownerA.Id, ObjectKey = $"{tenantA.Id:N}/{staged:N}/pending-a.pdf", FileName = "pending-a.pdf", ContentType = "application/pdf", Size = 4, Status = AttachmentStatus.Staged, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) },
                new Attachment { Id = expired, TenantId = tenantA.Id, UploaderId = ownerA.Id, ObjectKey = $"{tenantA.Id:N}/{expired:N}/expired-a.pdf", FileName = "expired-a.pdf", ContentType = "application/pdf", Size = 4, Status = AttachmentStatus.Staged, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
                new Attachment { Id = Guid.NewGuid(), TenantId = tenantB.Id, UploaderId = ownerB.Id, ObjectKey = $"{tenantB.Id:N}/foreign.pdf", FileName = "foreign.pdf", ContentType = "application/pdf", Size = 4, Status = AttachmentStatus.Ready, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
            await db.SaveChangesAsync();
        }
        var client = fixture.Factory.CreateClient();
        var tokenA = await LoginAsync(client, tenantA.Slug, ownerA.Email);
        var tokenB = await LoginAsync(client, tenantB.Slug, ownerB.Email);
        return new SeedData(client, tokenA, tokenB, ready, staged, expired);
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

    private static HttpRequestMessage Authorized(string token, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        return (await response.Content.ReadFromJsonAsync<ProblemResponse>())!;
    }

    private sealed record SeedData(HttpClient Client, string TokenA, string TokenB, Guid AttachmentAReady, Guid AttachmentAStaged, Guid AttachmentAExpired);
    private sealed record TokenResponse(string? AccessToken, DateTimeOffset? AccessTokenExpiresAt);
    private sealed record DownloadResponse(string? DownloadUrl, string? ContentType, string? FileName, DateTimeOffset? ExpiresAt);
    private sealed record ProblemResponse(string? Code, string? TraceId, string? Title, string? Detail);
}
