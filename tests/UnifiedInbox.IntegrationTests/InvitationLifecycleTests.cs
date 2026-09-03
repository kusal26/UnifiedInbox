using Microsoft.AspNetCore.Identity;
using Shouldly;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;
using UnifiedInbox.Infrastructure.Services;

namespace UnifiedInbox.IntegrationTests;

public sealed class InvitationLifecycleTests
{
    [Fact]
    public async Task Full_invite_accept_login_flow_works()
    {
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, ownerId, UserRole.Owner);
        TestContexts.SeedTenant(db, tenantId);
        TestContexts.SeedUser(db, tenantId, ownerId, UserRole.Owner, "owner@example.com");
        var mail = new FakeMailSender();
        var invitations = new InvitationService(db, new TestTenant(tenantId, ownerId, UserRole.Owner), new PasswordHasher<User>(), mail);

        var summary = await invitations.InviteAsync("agent@example.com", UserRole.Agent, CancellationToken.None);
        summary.Email.ShouldBe("agent@example.com");
        mail.Sent.ShouldHaveSingleItem();
        (await invitations.ListAsync(CancellationToken.None)).ShouldHaveSingleItem();

        var accepted = await invitations.AcceptAsync(mail.LastToken(), "Agent", "supersecure-password-1", CancellationToken.None);
        accepted.ShouldBeTrue();
        (await invitations.ListAsync(CancellationToken.None)).ShouldBeEmpty();

        var auth = new AuthenticationService(db, new PasswordHasher<User>(), new FakeTokenIssuer(), new TestTenant(tenantId, Guid.NewGuid()), mail);
        var tokens = await auth.LoginAsync("acme", "agent@example.com", "supersecure-password-1", CancellationToken.None);
        tokens.ShouldNotBeNull();
    }

    [Fact]
    public async Task Admin_cannot_invite_owners_and_agents_cannot_invite()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (adminDb, _) = TestContexts.Create(tenantId, adminId, UserRole.Admin, dbName);
        TestContexts.SeedUser(adminDb, tenantId, adminId, UserRole.Admin, "admin@example.com");
        TestContexts.SeedUser(adminDb, tenantId, agentId, UserRole.Agent, "agent@example.com");
        var adminInvites = new InvitationService(adminDb, new TestTenant(tenantId, adminId, UserRole.Admin), new PasswordHasher<User>(), new FakeMailSender());

        var forbidden = await Should.ThrowAsync<InboxException>(adminInvites.InviteAsync("owner@example.com", UserRole.Owner, CancellationToken.None));
        forbidden.StatusCode.ShouldBe(403);
        await adminInvites.InviteAsync("agent2@example.com", UserRole.Agent, CancellationToken.None);

        var (agentDb, _) = TestContexts.Create(tenantId, agentId, UserRole.Agent, dbName);
        var agentInvites = new InvitationService(agentDb, new TestTenant(tenantId, agentId, UserRole.Agent), new PasswordHasher<User>(), new FakeMailSender());
        await Should.ThrowAsync<UnauthorizedAccessException>(agentInvites.InviteAsync("x@example.com", UserRole.Agent, CancellationToken.None));
    }

    [Fact]
    public async Task Expired_revoked_and_double_accepts_fail_closed()
    {
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, ownerId, UserRole.Owner);
        TestContexts.SeedUser(db, tenantId, ownerId, UserRole.Owner, "owner@example.com");
        var mail = new FakeMailSender();
        var invitations = new InvitationService(db, new TestTenant(tenantId, ownerId, UserRole.Owner), new PasswordHasher<User>(), mail);

        var first = await invitations.InviteAsync("one@example.com", UserRole.Agent, CancellationToken.None);
        var firstToken = mail.LastToken();
        var second = await invitations.InviteAsync("one@example.com", UserRole.Agent, CancellationToken.None);
        second.Id.ShouldNotBe(first.Id); // re-invite revokes the previous pending token
        (await invitations.AcceptAsync(firstToken, "One", "supersecure-password-1", CancellationToken.None)).ShouldBeFalse();
        (await invitations.AcceptAsync(mail.LastToken(), "One", "supersecure-password-1", CancellationToken.None)).ShouldBeTrue();
        var replay = await Should.ThrowAsync<InboxException>(invitations.AcceptAsync(mail.LastToken(), "One", "supersecure-password-1", CancellationToken.None));
        replay.Code.ShouldBe("already_member");

        var revoked = await invitations.InviteAsync("two@example.com", UserRole.Agent, CancellationToken.None);
        var revokedToken = mail.LastToken();
        (await invitations.RevokeAsync(revoked.Id, CancellationToken.None)).ShouldBeTrue();
        (await invitations.AcceptAsync(revokedToken, "Two", "supersecure-password-1", CancellationToken.None)).ShouldBeFalse();
        (await invitations.AcceptAsync("not-a-real-token", "Nobody", "supersecure-password-1", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Inviting_an_existing_member_conflicts_and_cross_tenant_is_denied()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (dbA, _) = TestContexts.Create(tenantA, ownerA, UserRole.Owner, dbName);
        TestContexts.SeedUser(dbA, tenantA, ownerA, UserRole.Owner, "owner-a@example.com");
        TestContexts.SeedUser(dbA, tenantB, ownerB, UserRole.Owner, "owner-b@example.com");
        var invitesA = new InvitationService(dbA, new TestTenant(tenantA, ownerA, UserRole.Owner), new PasswordHasher<User>(), new FakeMailSender());

        var conflict = await Should.ThrowAsync<InboxException>(invitesA.InviteAsync("owner-a@example.com", UserRole.Agent, CancellationToken.None));
        conflict.Code.ShouldBe("already_member");

        var pending = await invitesA.InviteAsync("fresh@example.com", UserRole.Agent, CancellationToken.None);
        var (dbB, _) = TestContexts.Create(tenantB, ownerB, UserRole.Owner, dbName);
        var invitesB = new InvitationService(dbB, new TestTenant(tenantB, ownerB, UserRole.Owner), new PasswordHasher<User>(), new FakeMailSender());
        (await invitesB.ListAsync(CancellationToken.None)).ShouldBeEmpty();
        (await invitesB.RevokeAsync(pending.Id, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Invitation_writes_audit_records()
    {
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var (db, _) = TestContexts.Create(tenantId, ownerId, UserRole.Owner);
        TestContexts.SeedUser(db, tenantId, ownerId, UserRole.Owner, "owner@example.com");
        var mail = new FakeMailSender();
        var invitations = new InvitationService(db, new TestTenant(tenantId, ownerId, UserRole.Owner), new PasswordHasher<User>(), mail);

        var invite = await invitations.InviteAsync("audit@example.com", UserRole.Agent, CancellationToken.None);
        await invitations.AcceptAsync(mail.LastToken(), "Audit", "supersecure-password-1", CancellationToken.None);
        await invitations.RevokeAsync((await invitations.InviteAsync("gone@example.com", UserRole.Agent, CancellationToken.None)).Id, CancellationToken.None);

        var actions = db.AuditEntries.Select(x => x.Action).ToList();
        actions.ShouldContain("invitation.created");
        actions.ShouldContain("invitation.accepted");
        actions.ShouldContain("invitation.revoked");
        invite.ShouldNotBeNull();
    }
}
