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
/// Authorization matrix for the administration surface: role/lifecycle rules, canned responses,
/// notifications and preferences, metrics windows, workspace retention clamping, and the Owner-only
/// audit log. Runs over the real API host as <c>app_runtime</c>.
/// </summary>
[Collection("runtime-role")]
public sealed class AdministrationApiTests(RuntimeRoleFixture fixture)
{
    private const string Password = "supersecure-password-1";

    [DockerFact]
    public async Task Administration_routes_require_authentication()
    {
        using var client = fixture.Factory.CreateClient();
        var id = Guid.NewGuid();
        var anonymous = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Get, "/api/v1/users"),
            (HttpMethod.Get, "/api/v1/channels"),
            (HttpMethod.Get, "/api/v1/canned-responses"),
            (HttpMethod.Post, "/api/v1/canned-responses"),
            (HttpMethod.Put, $"/api/v1/canned-responses/{id}"),
            (HttpMethod.Delete, $"/api/v1/canned-responses/{id}"),
            (HttpMethod.Get, "/api/v1/notifications"),
            (HttpMethod.Put, "/api/v1/notification-preferences"),
            (HttpMethod.Get, "/api/v1/audit-logs"),
            (HttpMethod.Get, "/api/v1/audit-logs/export"),
            (HttpMethod.Get, "/api/v1/metrics/overview"),
            (HttpMethod.Get, "/api/v1/workspace"),
            (HttpMethod.Put, "/api/v1/workspace"),
        };
        foreach (var (method, url) in anonymous)
        {
            using var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post && url.EndsWith("/canned-responses")) request.Content = JsonContent.Create(new { title = "Hi", shortcut = "/hi", content = "Hello" });
            if (method == HttpMethod.Put && url.Contains("/canned-responses/")) request.Content = JsonContent.Create(new { title = "Hi", shortcut = "/hi", content = "Hello" });
            if (method == HttpMethod.Put && url.EndsWith("/notification-preferences")) request.Content = JsonContent.Create(new { kind = "message.received", enabled = true });
            if (method == HttpMethod.Put && url.EndsWith("/workspace")) request.Content = JsonContent.Create(new { name = "N", retentionDays = 30 });
            (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [DockerFact]
    public async Task Agents_are_forbidden_from_admin_and_owner_endpoints_with_stable_problems()
    {
        var seed = await SeedAsync();
        var adminDenied = new (string Token, HttpMethod Method, string Url, bool WithBody)[]
        {
            (seed.AgentToken, HttpMethod.Get, "/api/v1/users", false),
            (seed.AgentToken, HttpMethod.Get, "/api/v1/channels", false),
            (seed.AgentToken, HttpMethod.Post, "/api/v1/canned-responses", true),
            (seed.AgentToken, HttpMethod.Put, $"/api/v1/canned-responses/{Guid.NewGuid()}", true),
            (seed.AgentToken, HttpMethod.Delete, $"/api/v1/canned-responses/{Guid.NewGuid()}", false),
            (seed.AgentToken, HttpMethod.Get, "/api/v1/metrics/overview", false),
            (seed.AgentToken, HttpMethod.Put, "/api/v1/workspace", true),
            (seed.AdminToken, HttpMethod.Put, $"/api/v1/users/{seed.OwnerId}/role", true),
            (seed.AdminToken, HttpMethod.Get, "/api/v1/audit-logs", false),
            (seed.AgentToken, HttpMethod.Get, "/api/v1/audit-logs", false),
        };
        foreach (var (token, method, url, withBody) in adminDenied)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (withBody)
            {
                if (url.EndsWith("/workspace")) request.Content = JsonContent.Create(new { name = "N", retentionDays = 30 });
                else if (url.EndsWith("/role")) request.Content = JsonContent.Create(new { role = (int)UserRole.Agent });
                else request.Content = JsonContent.Create(new { title = "Hi", shortcut = "/hi", content = "Hello" });
            }
            var response = await seed.Client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var problem = await ReadProblemAsync(response);
            problem.Code.ShouldBe("forbidden");
            problem.TraceId.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [DockerFact]
    public async Task Admins_can_list_users_channels_and_read_metrics()
    {
        var seed = await SeedAsync();
        using (var users = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users"))
        {
            users.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            var response = await seed.Client.SendAsync(users);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            body.ShouldContain(seed.OwnerEmail);
            body.ShouldContain(seed.AgentEmail);
            body.ShouldNotContain(seed.ForeignOwnerEmail);
            // No credential material may be serialized to team members.
            body.ShouldNotContain("PasswordHash", Case.Insensitive);
            body.ShouldNotContain("passwordHash", Case.Insensitive);
            body.ShouldNotContain("NormalizedEmail", Case.Insensitive);
            body.ShouldNotContain("normalizedEmail", Case.Insensitive);
        }
        using (var channels = new HttpRequestMessage(HttpMethod.Get, "/api/v1/channels"))
        {
            channels.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            var response = await seed.Client.SendAsync(channels);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).ShouldContain("Sales Channel");
        }
        using (var metrics = new HttpRequestMessage(HttpMethod.Get, "/api/v1/metrics/overview"))
        {
            metrics.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            var response = await seed.Client.SendAsync(metrics);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<MetricsResponse>())!.Days.ShouldBe(30);
        }
    }

    [DockerFact]
    public async Task Owner_manages_roles_with_audit_and_cannot_change_own_role()
    {
        var seed = await SeedAsync();

        // Promote the agent to Admin, then demote back, verifying audit rows are written.
        using (var promote = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{seed.AgentId}/role"))
        {
            promote.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
            promote.Content = JsonContent.Create(new { role = (int)UserRole.Admin });
            (await seed.Client.SendAsync(promote)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            (await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.AgentId)).Role.ShouldBe(UserRole.Admin);
            var audit = await db.AuditEntries.IgnoreQueryFilters()
                .Where(x => x.TenantId == seed.TenantId && x.Action == "user.role.changed" && x.ActorId == seed.OwnerId).ToListAsync();
            var entry = audit.ShouldHaveSingleItem();
            entry.Resource.ShouldBe(seed.AgentId.ToString());
            entry.Metadata.ShouldContain("\"role\":\"Admin\"");
        }
        using (var demote = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{seed.AgentId}/role"))
        {
            demote.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
            demote.Content = JsonContent.Create(new { role = (int)UserRole.Agent });
            (await seed.Client.SendAsync(demote)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            (await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.AgentId)).Role.ShouldBe(UserRole.Agent);
        }

        // An Owner cannot change their own role.
        using (var self = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{seed.OwnerId}/role"))
        {
            self.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
            self.Content = JsonContent.Create(new { role = (int)UserRole.Agent });
            var response = await seed.Client.SendAsync(self);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReadProblemAsync(response)).Code.ShouldBe("cannot_change_own_role");
        }

        // Cross-tenant role changes are invisible (404), never leaking the target user's existence.
        using (var foreign = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{seed.ForeignOwnerId}/role"))
        {
            foreign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
            foreign.Content = JsonContent.Create(new { role = (int)UserRole.Admin });
            var response = await seed.Client.SendAsync(foreign);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await ReadProblemAsync(response)).Code.ShouldBe("user_not_found");
        }

        // A malformed role value is a stable [ApiController] 400.
        using (var malformed = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{seed.AgentId}/role"))
        {
            malformed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
            malformed.Content = JsonContent.Create(new { role = "Superuser" });
            var response = await seed.Client.SendAsync(malformed);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).ShouldContain("\"code\":\"invalid_request\"");
        }
    }

    [DockerFact]
    public async Task User_lifecycle_rules_apply_to_admins()
    {
        var seed = await SeedAsync();
        // An Admin cannot deactivate an Owner.
        using (var deactivateOwner = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{seed.OwnerId}/active"))
        {
            deactivateOwner.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            deactivateOwner.Content = JsonContent.Create(new { isActive = false });
            var response = await seed.Client.SendAsync(deactivateOwner);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ReadProblemAsync(response)).Code.ShouldBe("user_lifecycle_forbidden");
        }
        // An Admin cannot deactivate themselves.
        using (var self = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{seed.AdminId}/active"))
        {
            self.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            self.Content = JsonContent.Create(new { isActive = false });
            var response = await seed.Client.SendAsync(self);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReadProblemAsync(response)).Code.ShouldBe("cannot_deactivate_self");
        }
        // An Admin CAN deactivate and reactivate an Agent; deactivation revokes the agent's sessions.
        using (var deactivate = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{seed.AgentId}/active"))
        {
            deactivate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            deactivate.Content = JsonContent.Create(new { isActive = false });
            (await seed.Client.SendAsync(deactivate)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            (await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.AgentId)).IsActive.ShouldBeFalse();
            (await db.RefreshTokens.IgnoreQueryFilters().SingleAsync(x => x.UserId == seed.AgentId)).RevokedAt.ShouldNotBeNull();
            var audit = await db.AuditEntries.IgnoreQueryFilters()
                .Where(x => x.TenantId == seed.TenantId && x.Action == "user.deactivated" && x.ActorId == seed.AdminId).ToListAsync();
            audit.ShouldHaveSingleItem();
        }
        using (var reactivate = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{seed.AgentId}/active"))
        {
            reactivate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            reactivate.Content = JsonContent.Create(new { isActive = true });
            (await seed.Client.SendAsync(reactivate)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            (await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.AgentId)).IsActive.ShouldBeTrue();
        }
    }

    [DockerFact]
    public async Task Canned_response_crud_is_admin_scoped_and_validated()
    {
        var seed = await SeedAsync();

        // Create -> list -> update -> delete as Admin.
        Guid cannedId;
        using (var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/canned-responses"))
        {
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            create.Content = JsonContent.Create(new { title = "Greeting", shortcut = "/hi", content = "Hello there!" });
            var response = await seed.Client.SendAsync(create);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            cannedId = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
        }
        using (var list = new HttpRequestMessage(HttpMethod.Get, "/api/v1/canned-responses?q=Greeting"))
        {
            list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AgentToken);
            var response = await seed.Client.SendAsync(list);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).ShouldContain("Hello there!");
        }
        using (var update = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/canned-responses/{cannedId}"))
        {
            update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            update.Content = JsonContent.Create(new { title = "Greeting", shortcut = "/hi", content = "Updated greeting!" });
            var response = await seed.Client.SendAsync(update);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).ShouldContain("Updated greeting!");
        }
        using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/canned-responses/{cannedId}"))
        {
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            (await seed.Client.SendAsync(delete)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // A missing canned response yields a stable 404 on update (problem) and delete (empty 404).
        using (var updateMissing = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/canned-responses/{Guid.NewGuid()}"))
        {
            updateMissing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            updateMissing.Content = JsonContent.Create(new { title = "Greeting", shortcut = "/hi", content = "X" });
            var response = await seed.Client.SendAsync(updateMissing);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await ReadProblemAsync(response)).Code.ShouldBe("canned_response_not_found");
        }
        using (var deleteMissing = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/canned-responses/{Guid.NewGuid()}"))
        {
            deleteMissing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            (await seed.Client.SendAsync(deleteMissing)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // Empty required text is a stable 400.
        using (var empty = new HttpRequestMessage(HttpMethod.Post, "/api/v1/canned-responses"))
        {
            empty.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            empty.Content = JsonContent.Create(new { title = "", shortcut = "/hi", content = "X" });
            var response = await seed.Client.SendAsync(empty);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReadProblemAsync(response)).Code.ShouldBe("invalid_request");
        }
    }

    [DockerFact]
    public async Task Notification_and_preference_flows_validate_kinds()
    {
        var seed = await SeedAsync();
        // An agent sees and reads tenant notifications.
        using (var read = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/notifications/{seed.NotificationId}/read"))
        {
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AgentToken);
            (await seed.Client.SendAsync(read)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
        using (var unread = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications?unreadOnly=true"))
        {
            unread.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AgentToken);
            var response = await seed.Client.SendAsync(unread);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).ShouldBe("[]");
        }
        using (var missing = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/notifications/{Guid.NewGuid()}/read"))
        {
            missing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AgentToken);
            (await seed.Client.SendAsync(missing)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        using (var readAll = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/read-all"))
        {
            readAll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AgentToken);
            (await seed.Client.SendAsync(readAll)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Preferences update and persist for a known kind.
        using (var set = new HttpRequestMessage(HttpMethod.Put, "/api/v1/notification-preferences"))
        {
            set.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AgentToken);
            set.Content = JsonContent.Create(new { kind = "message.received", enabled = false });
            var response = await seed.Client.SendAsync(set);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).ShouldContain("message.received");
        }
        using (var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notification-preferences"))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AgentToken);
            var response = await seed.Client.SendAsync(get);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).ShouldContain("message.received");
        }
        // An unknown preference kind is a stable 400.
        using (var unknown = new HttpRequestMessage(HttpMethod.Put, "/api/v1/notification-preferences"))
        {
            unknown.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AgentToken);
            unknown.Content = JsonContent.Create(new { kind = "channel.spam", enabled = true });
            var response = await seed.Client.SendAsync(unknown);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReadProblemAsync(response)).Code.ShouldBe("invalid_request");
        }
    }

    [DockerFact]
    public async Task Metrics_restrict_the_window_to_7_30_or_90_days()
    {
        var seed = await SeedAsync();
        foreach (var days in new[] { 7, 30, 90 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/metrics/overview?days={days}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            var response = await seed.Client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<MetricsResponse>())!.Days.ShouldBe(days);
        }
        using var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/v1/metrics/overview?days=45");
        invalid.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
        var rejected = await seed.Client.SendAsync(invalid);
        rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(rejected)).Code.ShouldBe("invalid_request");
    }

    [DockerFact]
    public async Task Workspace_updates_clamp_retention_days_and_are_audited()
    {
        var seed = await SeedAsync();
        using (var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/workspace"))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            var response = await seed.Client.SendAsync(get);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<WorkspaceResponse>();
            body!.Name.ShouldBe("Administration Workspace");
            body.RetentionDays.ShouldBe(365);
        }
        using (var low = new HttpRequestMessage(HttpMethod.Put, "/api/v1/workspace"))
        {
            low.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            low.Content = JsonContent.Create(new { name = "Renamed Workspace", retentionDays = 5 });
            var response = await seed.Client.SendAsync(low);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<WorkspaceResponse>();
            body!.Name.ShouldBe("Renamed Workspace");
            body.RetentionDays.ShouldBe(30); // clamped up to the minimum
        }
        using (var high = new HttpRequestMessage(HttpMethod.Put, "/api/v1/workspace"))
        {
            high.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.AdminToken);
            high.Content = JsonContent.Create(new { name = "Renamed Workspace", retentionDays = 5000 });
            var response = await seed.Client.SendAsync(high);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<WorkspaceResponse>())!.RetentionDays.ShouldBe(3650); // clamped down
        }
        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            var audit = await db.AuditEntries.IgnoreQueryFilters()
                .Where(x => x.TenantId == seed.TenantId && x.Action == "workspace.updated" && x.ActorId == seed.AdminId).ToListAsync();
            audit.Count.ShouldBe(2);
        }
    }

    [DockerFact]
    public async Task Audit_logs_are_owner_only_and_export_as_csv()
    {
        var seed = await SeedAsync();
        // Produce a role-change audit entry to query for.
        using (var promote = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/users/{seed.AgentId}/role"))
        {
            promote.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
            promote.Content = JsonContent.Create(new { role = (int)UserRole.Admin });
            (await seed.Client.SendAsync(promote)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var logs = new HttpRequestMessage(HttpMethod.Get, "/api/v1/audit-logs?q=user.role.changed"))
        {
            logs.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
            var response = await seed.Client.SendAsync(logs);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).ShouldContain("user.role.changed");
        }
        using (var export = new HttpRequestMessage(HttpMethod.Get, "/api/v1/audit-logs/export?q=user.role.changed"))
        {
            export.Headers.Authorization = new AuthenticationHeaderValue("Bearer", seed.OwnerToken);
            var response = await seed.Client.SendAsync(export);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
            response.Content.Headers.ContentDisposition!.FileName.ShouldBe("audit-logs.csv");
            var csv = await response.Content.ReadAsStringAsync();
            csv.ShouldContain("created_at,actor_id,action,resource,metadata");
            csv.ShouldContain("user.role.changed");
        }

        // Agent and Admin are both denied the Owner-only audit log.
        foreach (var token in new[] { seed.AgentToken, seed.AdminToken })
        {
            using var denied = new HttpRequestMessage(HttpMethod.Get, "/api/v1/audit-logs");
            denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await seed.Client.SendAsync(denied);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ReadProblemAsync(response)).Code.ShouldBe("forbidden");
        }
    }

    private async Task<SeedData> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tenant = new Tenant(Guid.NewGuid(), $"admin-{suffix}", "Administration Workspace");
        var foreign = new Tenant(Guid.NewGuid(), $"adminf-{suffix}", "Foreign Workspace");
        var owner = NewUser(tenant.Id, $"owner-{suffix}@example.com", "Owner", UserRole.Owner);
        var admin = NewUser(tenant.Id, $"admin-{suffix}@example.com", "Admin", UserRole.Admin);
        var agent = NewUser(tenant.Id, $"agent-{suffix}@example.com", "Agent", UserRole.Agent);
        var foreignOwner = NewUser(foreign.Id, $"foreign-{suffix}@example.com", "Foreign Owner", UserRole.Owner);
        var channelId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            db.Tenants.AddRange(tenant, foreign);
            db.Users.AddRange(owner, admin, agent, foreignOwner);
            db.Channels.Add(new Channel(channelId, tenant.Id, "whatsapp", $"1555admin{suffix}", true) { DisplayName = "Sales Channel", IsEnabled = true, Status = "connected" });
            db.Notifications.Add(new NotificationEntity { Id = notificationId, TenantId = tenant.Id, Type = "channel.unhealthy", Text = "A channel needs attention." });
            await db.SaveChangesAsync();
        }

        var client = fixture.Factory.CreateClient();
        var ownerToken = await LoginAsync(client, tenant.Slug, owner.Email);
        var adminToken = await LoginAsync(client, tenant.Slug, admin.Email);
        var agentToken = await LoginAsync(client, tenant.Slug, agent.Email);
        return new SeedData(client, tenant.Id, owner.Id, admin.Id, agent.Id, foreignOwner.Id, owner.Email, agent.Email, foreignOwner.Email, ownerToken, adminToken, agentToken, notificationId);
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

    private sealed record SeedData(HttpClient Client, Guid TenantId, Guid OwnerId, Guid AdminId, Guid AgentId, Guid ForeignOwnerId, string OwnerEmail, string AgentEmail, string ForeignOwnerEmail, string OwnerToken, string AdminToken, string AgentToken, Guid NotificationId);
    private sealed record TokenResponse(string? AccessToken, DateTimeOffset? AccessTokenExpiresAt);
    private sealed record MetricsResponse(int Days);
    private sealed record WorkspaceResponse(string Name, int RetentionDays);
    private sealed record ProblemResponse(string? Code, string? TraceId, string? Title, string? Detail);
}
