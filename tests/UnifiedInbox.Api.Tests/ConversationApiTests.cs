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
/// Authorization matrix for the conversations surface: list/detail/activity/note/message/status/
/// read/customer-notes happy paths plus idempotency, the messaging-window policy, template+attachment
/// rejection, cross-tenant isolation, and membership re-reads. Runs over the real API host as
/// <c>app_runtime</c>.
/// </summary>
[Collection("runtime-role")]
public sealed class ConversationApiTests(RuntimeRoleFixture fixture)
{
    private const string Password = "supersecure-password-1";

    [DockerFact]
    public async Task Conversation_endpoints_require_authentication()
    {
        using var client = fixture.Factory.CreateClient();
        var id = Guid.NewGuid();
        var anonymous = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Get, "/api/v1/conversations"),
            (HttpMethod.Get, $"/api/v1/conversations/{id}"),
            (HttpMethod.Get, $"/api/v1/conversations/{id}/activity"),
            (HttpMethod.Post, $"/api/v1/conversations/{id}/notes"),
            (HttpMethod.Post, $"/api/v1/conversations/{id}/messages"),
            (HttpMethod.Patch, $"/api/v1/conversations/{id}/status"),
            (HttpMethod.Put, $"/api/v1/conversations/{id}/read"),
            (HttpMethod.Put, $"/api/v1/conversations/{id}/customer-notes"),
        };
        foreach (var (method, url) in anonymous)
        {
            using var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post && url.EndsWith("/notes")) request.Content = JsonContent.Create(new { body = "hello" });
            if (method == HttpMethod.Post && url.EndsWith("/messages")) request.Content = JsonContent.Create(new { body = "hello" });
            if (method == HttpMethod.Patch) request.Content = JsonContent.Create(new { status = (int)ConversationStatus.Closed });
            if (method == HttpMethod.Put && url.EndsWith("/read")) request.Content = JsonContent.Create(new { throughSequence = 1 });
            if (method == HttpMethod.Put && url.EndsWith("/customer-notes")) request.Content = JsonContent.Create(new { notes = "VIP" });
            (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [DockerFact]
    public async Task Verified_agent_reads_notes_and_sends_across_the_conversation_lifecycle()
    {
        var seed = await SeedAsync();

        // Detail is readable for the agent's own tenant.
        using (var detail = Authorized(seed.AgentToken, HttpMethod.Get, $"/api/v1/conversations/{seed.OpenConversationId}"))
        {
            var response = await seed.Client.SendAsync(detail);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<ConversationDetailsResponse>();
            body!.ContactName.ShouldBe("Customer A");
            body.LastReadSequence.ShouldBe(0);
        }

        // An internal note is created (201 + activity item body).
        using (var note = Authorized(seed.AgentToken, HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/notes"))
        {
            note.Content = JsonContent.Create(new { body = "Following up on the request." });
            var response = await seed.Client.SendAsync(note);
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            (await response.Content.ReadAsStringAsync()).ShouldContain("Following up");
        }

        // A free-form outbound message inside the 24h window is accepted (202).
        using (var message = Authorized(seed.AgentToken, HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/messages"))
        {
            message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            message.Content = JsonContent.Create(new { body = "We are on it." });
            var response = await seed.Client.SendAsync(message);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await response.Content.ReadAsStringAsync()).ShouldContain("We are on it");
        }

        // Customer notes update succeeds (204).
        using (var customerNotes = Authorized(seed.AgentToken, HttpMethod.Put, $"/api/v1/conversations/{seed.OpenConversationId}/customer-notes"))
        {
            customerNotes.Content = JsonContent.Create(new { notes = "VIP customer" });
            (await seed.Client.SendAsync(customerNotes)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The conversation list returns the row with its contact/platform/preview and no leak.
        using (var list = Authorized(seed.AgentToken, HttpMethod.Get, "/api/v1/conversations"))
        {
            var response = await seed.Client.SendAsync(list);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<ConversationListResponse>();
            body!.Items.ShouldContain(item => item.ContactName == "Customer A" && item.Platform == "whatsapp" && item.Preview.Length >= 0);
        }

        // Activity lists the seeded inbound and the two outbound rows, newest first.
        using (var activity = Authorized(seed.AgentToken, HttpMethod.Get, $"/api/v1/conversations/{seed.OpenConversationId}/activity"))
        {
            var response = await seed.Client.SendAsync(activity);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<ActivityListResponse>();
            body!.Items.Select(item => item.Body).ShouldContain("We are on it.");
        }

        // Status change (200) and read-through (200) round out the lifecycle.
        using (var status = Authorized(seed.AgentToken, HttpMethod.Patch, $"/api/v1/conversations/{seed.OpenConversationId}/status"))
        {
            status.Content = JsonContent.Create(new { status = (int)ConversationStatus.Pending });
            var response = await seed.Client.SendAsync(status);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<ConversationSummaryItem>();
            body!.Status.ShouldBe((int)ConversationStatus.Pending);
        }
        using (var read = Authorized(seed.AgentToken, HttpMethod.Put, $"/api/v1/conversations/{seed.OpenConversationId}/read"))
        {
            read.Content = JsonContent.Create(new { throughSequence = 50 });
            var response = await seed.Client.SendAsync(read);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            var conversation = await db.Conversations.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.OpenConversationId);
            conversation.LastReadSequence.ShouldBe(50);
            var notes = await db.InternalNotes.IgnoreQueryFilters().Where(x => x.ConversationId == seed.OpenConversationId).ToListAsync();
            notes.Select(x => x.Body).ShouldContain("Following up on the request.");
            var outbound = await db.Messages.IgnoreQueryFilters().Where(x => x.ConversationId == seed.OpenConversationId && x.Direction == MessageDirection.Outbound).ToListAsync();
            outbound.Select(x => x.Body).ShouldContain("We are on it.");
            var contact = await db.Contacts.IgnoreQueryFilters().SingleAsync(x => x.Id == conversation.ContactId);
            contact.Notes.ShouldBe("VIP customer");
        }
    }

    [DockerFact]
    public async Task Cross_tenant_conversations_are_invisible_and_never_leak()
    {
        var seed = await SeedAsync();

        // Every tenant-B read/mutation of a tenant-A conversation id must 404 with no data leak.
        var reads = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Get, $"/api/v1/conversations/{seed.OpenConversationId}"),
            (HttpMethod.Get, $"/api/v1/conversations/{seed.OpenConversationId}/activity"),
            (HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/notes"),
            (HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/messages"),
            (HttpMethod.Patch, $"/api/v1/conversations/{seed.OpenConversationId}/status"),
            (HttpMethod.Put, $"/api/v1/conversations/{seed.OpenConversationId}/read"),
            (HttpMethod.Put, $"/api/v1/conversations/{seed.OpenConversationId}/customer-notes"),
        };
        foreach (var (method, url) in reads)
        {
            using var request = Authorized(seed.ForeignAgentToken, method, url);
            if (method == HttpMethod.Post && url.EndsWith("/notes")) request.Content = JsonContent.Create(new { body = "hello" });
            if (method == HttpMethod.Post && url.EndsWith("/messages"))
            {
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
                request.Content = JsonContent.Create(new { body = "hello" });
            }
            if (method == HttpMethod.Patch) request.Content = JsonContent.Create(new { status = (int)ConversationStatus.Closed });
            if (method == HttpMethod.Put && url.EndsWith("/read")) request.Content = JsonContent.Create(new { throughSequence = 1 });
            if (method == HttpMethod.Put && url.EndsWith("/customer-notes")) request.Content = JsonContent.Create(new { notes = "nope" });
            (await seed.Client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [DockerFact]
    public async Task Idempotency_key_is_required_and_invalid_message_bodies_fail_stably()
    {
        var seed = await SeedAsync();

        // A message without the Idempotency-Key header is a stable 400.
        using (var noKey = Authorized(seed.AgentToken, HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/messages"))
        {
            noKey.Content = JsonContent.Create(new { body = "hello" });
            var response = await seed.Client.SendAsync(noKey);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await ReadProblemAsync(response);
            problem.Code.ShouldBe("idempotency_key_required");
            problem.TraceId.ShouldNotBeNullOrWhiteSpace();
        }

        // An empty note body is a stable 400 invalid_request.
        using (var emptyNote = Authorized(seed.AgentToken, HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/notes"))
        {
            emptyNote.Content = JsonContent.Create(new { body = "" });
            var response = await seed.Client.SendAsync(emptyNote);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReadProblemAsync(response)).Code.ShouldBe("invalid_request");
        }

        // A free-form message with no body, template, or attachments is a stable 400 invalid_request.
        using (var emptyMessage = Authorized(seed.AgentToken, HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/messages"))
        {
            emptyMessage.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            emptyMessage.Content = JsonContent.Create(new { body = "" });
            var response = await seed.Client.SendAsync(emptyMessage);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReadProblemAsync(response)).Code.ShouldBe("invalid_request");
        }
    }

    [DockerFact]
    public async Task Free_form_sends_are_rejected_when_the_messaging_window_is_closed()
    {
        var seed = await SeedAsync();
        using var message = Authorized(seed.AgentToken, HttpMethod.Post, $"/api/v1/conversations/{seed.ClosedConversationId}/messages");
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        message.Content = JsonContent.Create(new { body = "Are you still there?" });
        var response = await seed.Client.SendAsync(message);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("messaging_window_closed");
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
    }

    [DockerFact]
    public async Task Templates_combined_with_attachments_are_rejected_stably()
    {
        var seed = await SeedAsync();
        using var message = Authorized(seed.AgentToken, HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/messages");
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        message.Content = JsonContent.Create(new
        {
            body = "",
            template = new { name = "welcome_offer", language = "en_US" },
            attachmentIds = new[] { Guid.NewGuid() },
        });
        var response = await seed.Client.SendAsync(message);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await ReadProblemAsync(response);
        problem.Code.ShouldBe("template_invalid");
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
    }

    [DockerFact]
    public async Task Deactivated_agent_is_denied_notes_and_status_by_the_membership_re_read()
    {
        var seed = await SeedAsync();
        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            var agent = await db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == seed.AgentId);
            agent.IsActive = false;
            await db.SaveChangesAsync();
        }

        using (var note = Authorized(seed.AgentToken, HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/notes"))
        {
            note.Content = JsonContent.Create(new { body = "sneaky" });
            var response = await seed.Client.SendAsync(note);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ReadProblemAsync(response)).Code.ShouldBe("forbidden");
        }
        using (var status = Authorized(seed.AgentToken, HttpMethod.Patch, $"/api/v1/conversations/{seed.OpenConversationId}/status"))
        {
            status.Content = JsonContent.Create(new { status = (int)ConversationStatus.Closed });
            var response = await seed.Client.SendAsync(status);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ReadProblemAsync(response)).Code.ShouldBe("forbidden");
        }
    }

    [DockerFact]
    public async Task Conversation_list_filters_search_and_pages_without_dropping_rows()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tenantId = Guid.NewGuid();
        var slug = $"convlist-{suffix}";
        var email = $"owner-{suffix}@example.com";
        var channelId = Guid.NewGuid();
        var contactIds = Enumerable.Range(1, 5).Select(_ => Guid.NewGuid()).ToArray();
        var conversationIds = Enumerable.Range(1, 5).Select(_ => Guid.NewGuid()).ToArray();

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            db.Tenants.Add(new Tenant(tenantId, slug, "Conversation List"));
            db.Users.Add(NewUser(tenantId, email, "Owner", UserRole.Owner));
            db.Channels.Add(new Channel(channelId, tenantId, "whatsapp", $"1555list{suffix}", true) { DisplayName = "Sales", IsEnabled = true, Status = "connected" });
            for (var index = 0; index < 5; index += 1)
            {
                db.Contacts.Add(new Contact(contactIds[index], tenantId, "whatsapp", $"1555list{suffix}", $"cust-{index}-{suffix}", $"Customer {index}", $"+1555list{index}"));
                db.Conversations.Add(new Conversation { Id = conversationIds[index], TenantId = tenantId, ChannelId = channelId, ContactId = contactIds[index], ExternalConversationId = $"cust-{index}-{suffix}", Status = ConversationStatus.Open, UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(index), LastCustomerMessageAt = DateTimeOffset.UtcNow.AddMinutes(index) });
                db.Messages.Add(new Message { Id = Guid.NewGuid(), TenantId = tenantId, ChannelId = channelId, ConversationId = conversationIds[index], Direction = MessageDirection.Inbound, Body = $"hello {index}", ExternalMessageId = $"wamid.{index}.{suffix}", Sequence = index + 1, Status = MessageStatus.Sent });
            }
            await db.SaveChangesAsync();
        }

        var client = fixture.Factory.CreateClient();
        var token = await LoginAsync(client, slug, email);

        // Search by contact name and by message body.
        var (searched, _) = await GetConversationsAsync(client, token, "/api/v1/conversations?search=customer%204");
        searched.Items.Select(item => item.Id).ShouldBe([conversationIds[4].ToString()]);
        var (bodySearch, _) = await GetConversationsAsync(client, token, "/api/v1/conversations?search=hello%202");
        bodySearch.Items.Select(item => item.Id).ShouldBe([conversationIds[2].ToString()]);

        // Keyset pagination over pageSize=2 must yield the exact full unfiltered order with no
        // dropped or duplicated rows (UpdatedAt may tie, so compare against a single full read).
        var (full, _) = await GetConversationsAsync(client, token, "/api/v1/conversations?pageSize=100");
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var url = $"/api/v1/conversations?pageSize=2{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var (page, next) = await GetConversationsAsync(client, token, url);
            seen.AddRange(page.Items.Select(item => item.Id));
            cursor = next;
        } while (cursor is not null);
        seen.ShouldBe(full.Items.Select(item => item.Id).ToList());
    }

    [DockerFact]
    public async Task Conversation_activity_interleaves_messages_and_notes_in_sequence_order()
    {
        var seed = await SeedAsync();
        // Add outbound messages and internal notes with interleaved (strictly increasing) sequences.
        var outboundBodies = new List<string> { "outbound one", "outbound two" };
        var noteBodies = new List<string> { "note one", "note two" };
        foreach (var body in outboundBodies)
        {
            using var request = Authorized(seed.AgentToken, HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/messages");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            request.Content = JsonContent.Create(new { body });
            (await seed.Client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }
        foreach (var body in noteBodies)
        {
            using var request = Authorized(seed.AgentToken, HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/notes");
            request.Content = JsonContent.Create(new { body });
            (await seed.Client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }
        foreach (var body in outboundBodies)
        {
            using var request = Authorized(seed.AgentToken, HttpMethod.Post, $"/api/v1/conversations/{seed.OpenConversationId}/messages");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            request.Content = JsonContent.Create(new { body });
            (await seed.Client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        // Full activity (limit 50) returns the seeded inbound plus all six rows, newest sequence first.
        var all = await GetActivityAsync(seed.Client, seed.AgentToken, $"/api/v1/conversations/{seed.OpenConversationId}/activity?limit=50");
        var sequences = all.Items.Select(item => (long)item.Sequence).ToList();
        sequences.ShouldBe(sequences.OrderByDescending(x => x).ToList());
        all.Items.Select(item => item.Body).ShouldContain("outbound one");
        all.Items.Select(item => item.Body).ShouldContain("note two");

        // Cursor paging over a small limit must not drop or duplicate any item across pages.
        var seenSequences = new List<long>();
        string? cursor = null;
        do
        {
            var url = $"/api/v1/conversations/{seed.OpenConversationId}/activity?limit=2{(cursor is null ? "" : $"&before={cursor}")}";
            var page = await GetActivityAsync(seed.Client, seed.AgentToken, url);
            seenSequences.AddRange(page.Items.Select(item => (long)item.Sequence));
            cursor = page.NextCursor;
        } while (cursor is not null);
        seenSequences.ShouldBe(sequences);
    }

    private async Task<SeedData> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tenantA = new Tenant(Guid.NewGuid(), $"conv-{suffix}", "Conversations A");
        var tenantB = new Tenant(Guid.NewGuid(), $"convf-{suffix}", "Conversations B");
        var owner = NewUser(tenantA.Id, $"owner-{suffix}@example.com", "Owner", UserRole.Owner);
        var agent = NewUser(tenantA.Id, $"agent-{suffix}@example.com", "Agent", UserRole.Agent);
        var foreignAgent = NewUser(tenantB.Id, $"foreign-{suffix}@example.com", "Foreign Agent", UserRole.Agent);

        var channelOpenId = Guid.NewGuid();
        var channelClosedId = Guid.NewGuid();
        var contactOpenId = Guid.NewGuid();
        var contactClosedId = Guid.NewGuid();
        var openConversationId = Guid.NewGuid();
        var closedConversationId = Guid.NewGuid();
        var inboundMessageId = Guid.NewGuid();

        await using (var db = fixture.Context(fixture.OwnerConnection))
        {
            db.Tenants.AddRange(tenantA, tenantB);
            db.Users.AddRange(owner, agent, foreignAgent);
            db.Channels.AddRange(
                new Channel(channelOpenId, tenantA.Id, "whatsapp", $"1555open{suffix}", true) { DisplayName = "Sales", IsEnabled = true, Status = "connected" },
                new Channel(channelClosedId, tenantA.Id, "whatsapp", $"1555close{suffix}", true) { DisplayName = "Support", IsEnabled = true, Status = "connected" });
            db.Contacts.AddRange(
                new Contact(contactOpenId, tenantA.Id, "whatsapp", $"1555open{suffix}", $"cust-open-{suffix}", "Customer A", $"+1555open{suffix}"),
                new Contact(contactClosedId, tenantA.Id, "whatsapp", $"1555close{suffix}", $"cust-close-{suffix}", "Customer B", $"+1555close{suffix}"));
            db.Conversations.AddRange(
                new Conversation { Id = openConversationId, TenantId = tenantA.Id, ChannelId = channelOpenId, ContactId = contactOpenId, ExternalConversationId = $"cust-open-{suffix}", Status = ConversationStatus.Open, LastCustomerMessageAt = DateTimeOffset.UtcNow },
                new Conversation { Id = closedConversationId, TenantId = tenantA.Id, ChannelId = channelClosedId, ContactId = contactClosedId, ExternalConversationId = $"cust-close-{suffix}", Status = ConversationStatus.Open, LastCustomerMessageAt = null });
            db.Messages.Add(new Message
            {
                Id = inboundMessageId,
                TenantId = tenantA.Id,
                ChannelId = channelOpenId,
                ConversationId = openConversationId,
                Direction = MessageDirection.Inbound,
                Body = "I need help",
                ExternalMessageId = $"wamid.open.{suffix}",
                Sequence = 1,
                Status = MessageStatus.Sent,
                ProviderTimestamp = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var client = fixture.Factory.CreateClient();
        var ownerToken = await LoginAsync(client, tenantA.Slug, owner.Email);
        var agentToken = await LoginAsync(client, tenantA.Slug, agent.Email);
        var foreignAgentToken = await LoginAsync(client, tenantB.Slug, foreignAgent.Email);
        return new SeedData(client, tenantA.Id, agent.Id, openConversationId, closedConversationId, ownerToken, agentToken, foreignAgentToken);
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

    private async Task<(ConversationListResponse Page, string? NextCursor)> GetConversationsAsync(HttpClient client, string token, string url)
    {
        using var request = Authorized(token, HttpMethod.Get, url);
        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.TryGetValues("X-Next-Cursor", out var next);
        return ((await response.Content.ReadFromJsonAsync<ConversationListResponse>())!, next?.FirstOrDefault());
    }

    private async Task<ActivityListResponse> GetActivityAsync(HttpClient client, string token, string url)
    {
        using var request = Authorized(token, HttpMethod.Get, url);
        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ActivityListResponse>())!;
    }

    private sealed record SeedData(HttpClient Client, Guid TenantId, Guid AgentId, Guid OpenConversationId, Guid ClosedConversationId, string OwnerToken, string AgentToken, string ForeignAgentToken);
    private sealed record TokenResponse(string? AccessToken, DateTimeOffset? AccessTokenExpiresAt);
    private sealed record ConversationDetailsResponse(string ContactName, string Platform, string Phone, long LastReadSequence, string? CustomerNotes);
    private sealed record ConversationListResponse(ConversationSummaryItem[] Items, string? NextCursor);
    private sealed record ConversationSummaryItem(string Id, string ContactName, string Platform, string Preview, int Status, bool Unread, string UpdatedAt);
    private sealed record ActivityListResponse(ActivityItemResponse[] Items, string? NextCursor);
    private sealed record ActivityItemResponse(string Id, string Body, int Kind, int Sequence);
    private sealed record ProblemResponse(string? Code, string? TraceId, string? Title, string? Detail);
}
