# Unified Inbox MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the PRD-defined multi-tenant shared inbox MVP with authentication, tenant-safe conversation management, durable inbound and outbound WhatsApp messaging, real-time collaboration, internal notes, attachments, search, filtering, unread state, channel health, and operational recovery.

**Architecture:** Build a .NET 10 modular monolith with separate API and worker processes around Domain, Application, and Infrastructure projects. PostgreSQL is the source of truth; a transactional outbox bridges database commits to RabbitMQ, Redis supports SignalR scale-out and rate limiting, and React consumes REST plus tenant-scoped SignalR events. Deliver the product as vertical slices so each task leaves a verifiable capability.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core 10, PostgreSQL, RabbitMQ, Redis, SignalR, xUnit, Testcontainers, React 19, TypeScript, Vite, Tailwind CSS, TanStack Query, React Router, Vitest, Testing Library, Playwright, Docker Compose.

---

## Scope and resolved design decisions

This plan covers PRD phases 1-6 and the canned-response/attachment/browser-notification items needed by the MVP experience. Billing, subscriptions, advanced analytics, typing indicators, additional providers, and native mobile push remain outside this plan.

The following decisions supersede ambiguous or unsafe passages in the technical design:

1. Both inbound and outbound database-to-broker handoffs use a transactional outbox. A periodic recovery dispatcher handles stranded pending rows.
2. Webhook endpoints persist immutable request bytes and metadata. Workers parse `PersistedWebhookPayload`; `HttpRequest` never crosses the queue boundary.
3. Attachments are uploaded into a tenant/user-owned staged state, then atomically claimed by a new outbound message. Unclaimed uploads expire.
4. Conversation history is a single cursor-paginated activity timeline containing messages and internal notes in chronological order.
5. Contact identities are unique by tenant, platform, external business account, and external customer ID. `ExternalAccountId` is never omitted from fallback lookup.
6. Inbound message deduplication is database-enforced by `(ChannelId, ExternalMessageId)`, independent of conversation mapping.
7. Login requires `tenantSlug + email + password`; the same email may exist in multiple tenants.
8. WhatsApp free-form messages are rejected outside the customer-service window unless an approved template is selected. Provider idempotency/reconciliation is used where supported; ambiguous sends remain recoverable instead of being blindly retried.

## Target repository layout

```text
UnifiedInbox.slnx
Directory.Build.props
Directory.Packages.props
docker-compose.yml
.env.example
src/
  backend/
    UnifiedInbox.Domain/
    UnifiedInbox.Application/
    UnifiedInbox.Infrastructure/
    UnifiedInbox.Api/
    UnifiedInbox.Worker/
  frontend/
tests/
  UnifiedInbox.Domain.Tests/
  UnifiedInbox.Application.Tests/
  UnifiedInbox.IntegrationTests/
  UnifiedInbox.ArchitectureTests/
  e2e/
```

## Task 1: Scaffold the solution and local dependencies

**Files:**
- Create: `UnifiedInbox.slnx`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `docker-compose.yml`
- Create: `.env.example`
- Create: `src/backend/UnifiedInbox.Domain/UnifiedInbox.Domain.csproj`
- Create: `src/backend/UnifiedInbox.Application/UnifiedInbox.Application.csproj`
- Create: `src/backend/UnifiedInbox.Infrastructure/UnifiedInbox.Infrastructure.csproj`
- Create: `src/backend/UnifiedInbox.Api/UnifiedInbox.Api.csproj`
- Create: `src/backend/UnifiedInbox.Worker/UnifiedInbox.Worker.csproj`
- Create: `tests/UnifiedInbox.Domain.Tests/UnifiedInbox.Domain.Tests.csproj`
- Create: `tests/UnifiedInbox.Application.Tests/UnifiedInbox.Application.Tests.csproj`
- Create: `tests/UnifiedInbox.IntegrationTests/UnifiedInbox.IntegrationTests.csproj`
- Create: `tests/UnifiedInbox.ArchitectureTests/UnifiedInbox.ArchitectureTests.csproj`
- Create: `src/frontend/package.json`

- [ ] **Step 1: Create the solution and projects**

Run:

```powershell
dotnet new sln --format slnx -n UnifiedInbox
dotnet new classlib -n UnifiedInbox.Domain -o src/backend/UnifiedInbox.Domain -f net10.0
dotnet new classlib -n UnifiedInbox.Application -o src/backend/UnifiedInbox.Application -f net10.0
dotnet new classlib -n UnifiedInbox.Infrastructure -o src/backend/UnifiedInbox.Infrastructure -f net10.0
dotnet new webapi -n UnifiedInbox.Api -o src/backend/UnifiedInbox.Api -f net10.0 --use-controllers
dotnet new worker -n UnifiedInbox.Worker -o src/backend/UnifiedInbox.Worker -f net10.0
dotnet new xunit -n UnifiedInbox.Domain.Tests -o tests/UnifiedInbox.Domain.Tests -f net10.0
dotnet new xunit -n UnifiedInbox.Application.Tests -o tests/UnifiedInbox.Application.Tests -f net10.0
dotnet new xunit -n UnifiedInbox.IntegrationTests -o tests/UnifiedInbox.IntegrationTests -f net10.0
dotnet new xunit -n UnifiedInbox.ArchitectureTests -o tests/UnifiedInbox.ArchitectureTests -f net10.0
```

Expected: every template reports successful creation.

- [ ] **Step 2: Add projects and enforce dependency direction**

Run:

```powershell
dotnet sln UnifiedInbox.slnx add (Get-ChildItem src/backend,tests -Recurse -Filter *.csproj).FullName
dotnet add src/backend/UnifiedInbox.Application reference src/backend/UnifiedInbox.Domain
dotnet add src/backend/UnifiedInbox.Infrastructure reference src/backend/UnifiedInbox.Application src/backend/UnifiedInbox.Domain
dotnet add src/backend/UnifiedInbox.Api reference src/backend/UnifiedInbox.Application src/backend/UnifiedInbox.Infrastructure
dotnet add src/backend/UnifiedInbox.Worker reference src/backend/UnifiedInbox.Application src/backend/UnifiedInbox.Infrastructure
dotnet add tests/UnifiedInbox.Domain.Tests reference src/backend/UnifiedInbox.Domain
dotnet add tests/UnifiedInbox.Application.Tests reference src/backend/UnifiedInbox.Application src/backend/UnifiedInbox.Domain
dotnet add tests/UnifiedInbox.IntegrationTests reference src/backend/UnifiedInbox.Api src/backend/UnifiedInbox.Infrastructure
dotnet add tests/UnifiedInbox.ArchitectureTests reference src/backend/UnifiedInbox.Domain src/backend/UnifiedInbox.Application src/backend/UnifiedInbox.Infrastructure src/backend/UnifiedInbox.Api
```

Expected: each reference is added without a circular dependency.

- [ ] **Step 3: Add shared build settings**

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest</AnalysisLevel>
  </PropertyGroup>
</Project>
```

Create `Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.10" />
    <PackageVersion Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.0.10" />
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.10" />
    <PackageVersion Include="Microsoft.AspNetCore.SignalR.StackExchangeRedis" Version="10.0.10" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.10" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.10" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
    <PackageVersion Include="RabbitMQ.Client" Version="7.2.1" />
    <PackageVersion Include="StackExchange.Redis" Version="3.0.17" />
    <PackageVersion Include="FluentValidation.DependencyInjectionExtensions" Version="12.1.1" />
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.17.0" />
    <PackageVersion Include="Minio" Version="7.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.13.0" />
    <PackageVersion Include="Testcontainers.RabbitMq" Version="4.13.0" />
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
  </ItemGroup>
</Project>
```

Remove `Version` attributes from the `PackageReference` elements generated by the project templates, then add only the dependencies each project uses:

```powershell
dotnet add src/backend/UnifiedInbox.Infrastructure package Microsoft.AspNetCore.Identity.EntityFrameworkCore
dotnet add src/backend/UnifiedInbox.Infrastructure package Microsoft.EntityFrameworkCore
dotnet add src/backend/UnifiedInbox.Infrastructure package Microsoft.EntityFrameworkCore.Design
dotnet add src/backend/UnifiedInbox.Infrastructure package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/backend/UnifiedInbox.Infrastructure package RabbitMQ.Client
dotnet add src/backend/UnifiedInbox.Infrastructure package StackExchange.Redis
dotnet add src/backend/UnifiedInbox.Infrastructure package Minio
dotnet add src/backend/UnifiedInbox.Api package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add src/backend/UnifiedInbox.Api package Microsoft.AspNetCore.OpenApi
dotnet add src/backend/UnifiedInbox.Api package Microsoft.AspNetCore.SignalR.StackExchangeRedis
dotnet add src/backend/UnifiedInbox.Api package FluentValidation.DependencyInjectionExtensions
dotnet add src/backend/UnifiedInbox.Api package OpenTelemetry.Extensions.Hosting
dotnet add src/backend/UnifiedInbox.Api package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add src/backend/UnifiedInbox.Api package OpenTelemetry.Instrumentation.AspNetCore
dotnet add src/backend/UnifiedInbox.Api package OpenTelemetry.Instrumentation.Http
dotnet add src/backend/UnifiedInbox.Worker package OpenTelemetry.Extensions.Hosting
dotnet add src/backend/UnifiedInbox.Worker package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add tests/UnifiedInbox.Domain.Tests package Shouldly
dotnet add tests/UnifiedInbox.Application.Tests package Shouldly
dotnet add tests/UnifiedInbox.IntegrationTests package Shouldly
dotnet add tests/UnifiedInbox.IntegrationTests package Testcontainers.PostgreSql
dotnet add tests/UnifiedInbox.IntegrationTests package Testcontainers.RabbitMq
dotnet add tests/UnifiedInbox.ArchitectureTests package Shouldly
dotnet add tests/UnifiedInbox.ArchitectureTests package NetArchTest.Rules
```

Before the first restore, verify that every pin still exists and has no known critical advisory; update the pin and this file together if a package has been withdrawn.

- [ ] **Step 4: Add local infrastructure**

Create `docker-compose.yml`:

```yaml
services:
  postgres:
    image: postgres:18
    environment:
      POSTGRES_DB: unified_inbox
      POSTGRES_USER: unified_inbox
      POSTGRES_PASSWORD: local_only_password
    ports: ["5432:5432"]
    volumes: ["postgres-data:/var/lib/postgresql/data"]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U unified_inbox"]
      interval: 5s
      timeout: 5s
      retries: 10
  rabbitmq:
    image: rabbitmq:4-management
    ports: ["5672:5672", "15672:15672"]
    volumes: ["rabbitmq-data:/var/lib/rabbitmq"]
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]
      interval: 5s
      timeout: 5s
      retries: 10
  redis:
    image: redis:8-alpine
    ports: ["6379:6379"]
    volumes: ["redis-data:/data"]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 5s
      timeout: 5s
      retries: 10
  minio:
    image: minio/minio
    command: server /data --console-address :9001
    environment:
      MINIO_ROOT_USER: minioadmin
      MINIO_ROOT_PASSWORD: minioadmin
    ports: ["9000:9000", "9001:9001"]
    volumes: ["minio-data:/data"]
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:9000/minio/health/live"]
      interval: 5s
      timeout: 5s
      retries: 10

volumes:
  postgres-data:
  rabbitmq-data:
  redis-data:
  minio-data:
```

Create `.env.example`:

```dotenv
ConnectionStrings__Database=Host=localhost;Port=5432;Database=unified_inbox;Username=unified_inbox;Password=local_only_password
RabbitMq__Host=localhost
RabbitMq__Port=5672
Redis__Connection=localhost:6379
ObjectStorage__Endpoint=http://localhost:9000
ObjectStorage__AccessKey=minioadmin
ObjectStorage__SecretKey=minioadmin
ObjectStorage__Bucket=unified-inbox-local
Jwt__Issuer=unified-inbox-local
Jwt__Audience=unified-inbox-local
Jwt__SigningKey=development-only-signing-key-32-chars
WhatsApp__AppSecret=
WhatsApp__AccessToken=
WhatsApp__VerifyToken=development-verify-token
```

Do not commit live provider tokens or production credentials.

- [ ] **Step 5: Scaffold the frontend**

Run:

```powershell
bun create vite src/frontend --template react-ts
Set-Location src/frontend
bun add @microsoft/signalr @tanstack/react-query react-router-dom zod
bun add -d tailwindcss @tailwindcss/vite vitest jsdom @testing-library/react @testing-library/user-event @playwright/test
Set-Location ../..
```

Add `"test": "vitest"` and `"test:run": "vitest --run"` to `src/frontend/package.json`. Expected: the package file contains `dev`, `build`, `test`, `test:run`, and `preview` scripts.

- [ ] **Step 6: Verify the empty solution**

Run:

```powershell
dotnet build UnifiedInbox.slnx
bun --cwd src/frontend run build
```

Expected: both commands exit 0 with no warnings.

- [ ] **Step 7: Commit**

```powershell
git add UnifiedInbox.slnx Directory.Build.props Directory.Packages.props docker-compose.yml .env.example src tests
git commit -m "build: scaffold unified inbox solution"
```

## Task 2: Define tenant-safe domain entities and invariants

**Files:**
- Create: `src/backend/UnifiedInbox.Domain/Common/Entity.cs`
- Create: `src/backend/UnifiedInbox.Domain/Common/ITenantScoped.cs`
- Create: `src/backend/UnifiedInbox.Domain/Tenants/Tenant.cs`
- Create: `src/backend/UnifiedInbox.Domain/Users/User.cs`
- Create: `src/backend/UnifiedInbox.Domain/Channels/Channel.cs`
- Create: `src/backend/UnifiedInbox.Domain/Contacts/Contact.cs`
- Create: `src/backend/UnifiedInbox.Domain/Contacts/ContactPlatformIdentity.cs`
- Create: `src/backend/UnifiedInbox.Domain/Conversations/Conversation.cs`
- Create: `src/backend/UnifiedInbox.Domain/Conversations/Message.cs`
- Create: `src/backend/UnifiedInbox.Domain/Conversations/InternalNote.cs`
- Create: `src/backend/UnifiedInbox.Domain/Attachments/AttachmentUpload.cs`
- Create: `src/backend/UnifiedInbox.Domain/Common/Enums.cs`
- Create: `src/backend/UnifiedInbox.Domain/CannedResponses/CannedResponse.cs`
- Test: `tests/UnifiedInbox.Domain.Tests/Conversations/ConversationTests.cs`
- Test: `tests/UnifiedInbox.Domain.Tests/Conversations/MessageTests.cs`
- Test: `tests/UnifiedInbox.Domain.Tests/Attachments/AttachmentUploadTests.cs`

- [ ] **Step 1: Write failing invariant tests**

Cover these exact cases:

```csharp
[Fact]
public void Closed_conversation_reopens_for_inbound_customer_message()
{
    var conversation = Conversation.Create(TenantId, ChannelId, ContactId, "external-thread");
    conversation.Close(StaffUserId, Clock.UtcNow);
    conversation.RecordInboundActivity(Clock.UtcNow.AddMinutes(1));
    conversation.Status.ShouldBe(ConversationStatus.Open);
}

[Fact]
public void Staff_message_requires_sender_user()
{
    var act = () => Message.CreateOutbound(TenantId, ChannelId, ConversationId, null, "hello", "idem-1", Clock.UtcNow);
    Should.Throw<DomainException>(act);
}

[Fact]
public void Staged_upload_can_only_be_claimed_once()
{
    var upload = AttachmentUpload.Stage(TenantId, StaffUserId, "key", "photo.jpg", "image/jpeg", 100, Clock.UtcNow);
    upload.Claim(MessageId, Clock.UtcNow);
    var act = () => upload.Claim(Guid.NewGuid(), Clock.UtcNow);
    Should.Throw<DomainException>(act);
}
```

- [ ] **Step 2: Run tests and verify the red state**

Run: `dotnet test tests/UnifiedInbox.Domain.Tests --filter "ConversationTests|MessageTests|AttachmentUploadTests"`

Expected: FAIL because the domain types do not exist.

- [ ] **Step 3: Implement focused entities and enums**

Use private setters, named factory methods, and behavior methods. Define these exact initial enums:

```csharp
public enum Role { Owner, Admin, Agent }
public enum Platform { FacebookMessenger, Instagram, WhatsApp, TikTok }
public enum ChannelStatus { Connected, Disconnected, Error, ReauthorizationRequired }
public enum ConversationStatus { Open, Pending, Closed }
public enum SenderType { Customer, Staff, System }
public enum Direction { Inbound, Outbound }
public enum DeliveryStatus { Pending, Sending, Unknown, Sent, Delivered, Read, Failed }
public enum WebhookEventStatus { Received, Processing, Processed, Failed, Ignored }
public enum AttachmentUploadStatus { Staged, Claimed, Expired }
public enum OutboxStatus { Pending, Processing, Processed, DeadLettered }
```

Every tenant-owned aggregate implements:

```csharp
public interface ITenantScoped
{
    Guid TenantId { get; }
}
```

`Message` must contain both `ChannelId` and `ConversationId`, while `AttachmentUpload` must model `Staged`, `Claimed`, and `Expired` states. `ContactPlatformIdentity` must require `ExternalAccountId` and store `PlatformUsername`; do not permit a fallback identity without the business account scope.

- [ ] **Step 4: Add domain invariant tests for no assignment**

Create an architecture assertion that the Domain assembly contains no type or property whose name matches `AssignedUser`, `ConversationAssignment`, or `OwnerUserId` under Conversations.

- [ ] **Step 5: Run domain tests**

Run: `dotnet test tests/UnifiedInbox.Domain.Tests`

Expected: PASS with all invariant tests green.

- [ ] **Step 6: Commit**

```powershell
git add src/backend/UnifiedInbox.Domain tests/UnifiedInbox.Domain.Tests
git commit -m "feat: add tenant-safe messaging domain"
```

## Task 3: Add PostgreSQL persistence and tenant isolation

**Files:**
- Create: `src/backend/UnifiedInbox.Application/Abstractions/ICurrentTenant.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Persistence/UnifiedInboxDbContext.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Persistence/Configurations/*.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Persistence/Migrations/*`
- Create: `src/backend/UnifiedInbox.Infrastructure/Persistence/Interceptors/AuditableEntityInterceptor.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Persistence/TenantIsolationTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Persistence/UniquenessConstraintTests.cs`

- [ ] **Step 1: Write failing PostgreSQL integration tests**

Use Testcontainers PostgreSQL and prove:

```csharp
[Fact]
public async Task Query_filter_never_returns_another_tenants_conversation()
{
    await SeedConversationAsync(TenantA, "a-thread");
    await SeedConversationAsync(TenantB, "b-thread");
    SetCurrentTenant(TenantA);
    (await Db.Conversations.Select(x => x.ExternalConversationId).ToListAsync())
        .ShouldBe(["a-thread"]);
}

[Fact]
public async Task External_message_id_is_unique_within_channel_across_conversations()
{
    await InsertMessageAsync(ChannelA, ConversationA, "wamid.1");
    var act = () => InsertMessageAsync(ChannelA, ConversationB, "wamid.1");
    await Should.ThrowAsync<DbUpdateException>(act);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/UnifiedInbox.IntegrationTests --filter "TenantIsolationTests|UniquenessConstraintTests"`

Expected: FAIL because the context and schema do not exist.

- [ ] **Step 3: Configure the DbContext and global filters**

`UnifiedInboxDbContext` must accept `ICurrentTenant`. Apply a query filter to every `ITenantScoped` entity and fail closed when no tenant is established for request-scoped operations. Background workers must explicitly establish a trusted tenant scope from a persisted `ChannelId` or `OutboxMessage.TenantId` before querying tenant data.

- [ ] **Step 4: Configure exact database constraints**

Add these unique indexes:

```csharp
channel.HasIndex(x => new { x.TenantId, x.Platform, x.ExternalAccountId }).IsUnique();
identity.HasIndex(x => new { x.TenantId, x.Platform, x.ExternalAccountId, x.ExternalPlatformUserId }).IsUnique();
conversation.HasIndex(x => new { x.ChannelId, x.ExternalConversationId }).IsUnique();
message.HasIndex(x => new { x.ChannelId, x.ExternalMessageId }).IsUnique()
    .HasFilter("\"ExternalMessageId\" IS NOT NULL");
message.HasIndex(x => new { x.ConversationId, x.IdempotencyKey }).IsUnique()
    .HasFilter("\"IdempotencyKey\" IS NOT NULL");
```

- [ ] **Step 5: Create and apply the initial migration**

Run:

```powershell
dotnet ef migrations add InitialMessagingSchema --project src/backend/UnifiedInbox.Infrastructure --startup-project src/backend/UnifiedInbox.Api --output-dir Persistence/Migrations
dotnet test tests/UnifiedInbox.IntegrationTests --filter "TenantIsolationTests|UniquenessConstraintTests"
```

Expected: migration succeeds and both tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/backend/UnifiedInbox.Application src/backend/UnifiedInbox.Infrastructure tests/UnifiedInbox.IntegrationTests
git commit -m "feat: enforce tenant isolation in persistence"
```

## Task 4: Implement tenant-aware authentication and RBAC

**Files:**
- Create: `src/backend/UnifiedInbox.Application/Auth/Register/RegisterCommand.cs`
- Create: `src/backend/UnifiedInbox.Application/Auth/Login/LoginCommand.cs`
- Create: `src/backend/UnifiedInbox.Application/Auth/Refresh/RefreshTokenCommand.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Identity/PasswordHasher.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Identity/JwtTokenService.cs`
- Create: `src/backend/UnifiedInbox.Api/Controllers/AuthController.cs`
- Create: `src/backend/UnifiedInbox.Api/Authorization/TenantAuthorizationHandler.cs`
- Create: `src/backend/UnifiedInbox.Api/Authorization/Policies.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Auth/AuthFlowTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Auth/AuthorizationTests.cs`

- [ ] **Step 1: Write failing auth flow tests**

Test registration creates `Tenant + Owner`, login requires `tenantSlug`, refresh tokens rotate, disabled users cannot log in, and the same email can log into two tenants only with the matching slug.

```csharp
var response = await Client.PostAsJsonAsync("/api/v1/auth/login", new
{
    tenantSlug = "acme",
    email = "owner@example.com",
    password = "Correct-Horse-9"
});
response.StatusCode.ShouldBe(HttpStatusCode.OK);
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/UnifiedInbox.IntegrationTests --filter "AuthFlowTests|AuthorizationTests"`

Expected: FAIL with 404 or missing handler errors.

- [ ] **Step 3: Implement registration, login, and refresh rotation**

Return short-lived access tokens containing `sub`, `tenant_id`, and `role`. Store only a hash of each refresh token, link replacements, revoke the used token in the same transaction, and reject replayed refresh tokens.

- [ ] **Step 4: Implement explicit authorization policies**

Define policies for `ManageWorkspace`, `ManageUsers`, `ManageChannels`, `ManageCannedResponses`, `WorkInbox`, `ViewChannelHealth`, and `ViewAuditLogs`. Do not encode phrases such as “where permitted”; map each role to exact policies in tests.

- [ ] **Step 5: Verify cross-tenant denial**

Add a request test using Tenant A’s JWT against Tenant B’s conversation ID. Expect `404 Not Found` to avoid confirming resource existence, and assert an audit/security log is written.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test tests/UnifiedInbox.IntegrationTests --filter "AuthFlowTests|AuthorizationTests"
git add src/backend tests/UnifiedInbox.IntegrationTests
git commit -m "feat: add tenant-aware authentication and authorization"
```

Expected: all selected tests pass.

## Task 5: Add the transactional outbox and RabbitMQ transport

**Files:**
- Create: `src/backend/UnifiedInbox.Domain/Messaging/OutboxMessage.cs`
- Create: `src/backend/UnifiedInbox.Application/Messaging/IOutboxWriter.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Messaging/OutboxWriter.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Messaging/RabbitMqPublisher.cs`
- Create: `src/backend/UnifiedInbox.Worker/Messaging/OutboxDispatcher.cs`
- Create: `src/backend/UnifiedInbox.Worker/Messaging/OutboxRecoveryService.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Persistence/UnifiedInboxDbContext.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Messaging/OutboxTests.cs`

- [ ] **Step 1: Write failing atomicity and recovery tests**

Prove that a domain row and its outbox record commit together, broker failure leaves the outbox record pending, and a later dispatch publishes it once and marks it dispatched.

```csharp
[Fact]
public async Task Broker_failure_does_not_lose_committed_outbox_message()
{
    Publisher.FailNextPublish();
    await Handler.Handle(CreatePendingOutboundMessage());
    await Dispatcher.RunOnce(CancellationToken.None);
    (await LoadOutbox()).Status.ShouldBe(OutboxStatus.Pending);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/UnifiedInbox.IntegrationTests --filter OutboxTests`

Expected: FAIL because outbox persistence and dispatch do not exist.

- [ ] **Step 3: Implement the outbox schema and writer**

Store `Id`, `TenantId`, `Type`, `PayloadJson`, `OccurredAt`, `AvailableAt`, `AttemptCount`, `LockedUntil`, `ProcessedAt`, and `LastError`. Insert outbox records inside the same EF Core transaction as business changes.

- [ ] **Step 4: Implement safe concurrent dispatch**

Claim batches with PostgreSQL `FOR UPDATE SKIP LOCKED`, publish persistent RabbitMQ messages with publisher confirms, and mark processed only after confirmation. On failure, increment attempts and set exponential backoff with jitter. Do not place the DLQ in Redis.

- [ ] **Step 5: Add recovery behavior**

`OutboxRecoveryService` must release expired locks and continuously retry eligible pending rows. After the configured maximum, publish to a RabbitMQ dead-letter exchange and retain the database record for inspection/replay.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test tests/UnifiedInbox.IntegrationTests --filter OutboxTests
git add src/backend tests/UnifiedInbox.IntegrationTests
git commit -m "feat: add durable transactional outbox"
```

Expected: atomicity, retry, and recovery tests pass.

## Task 6: Persist and enqueue validated webhook events

**Files:**
- Create: `src/backend/UnifiedInbox.Domain/Webhooks/WebhookEvent.cs`
- Create: `src/backend/UnifiedInbox.Application/Channels/IWebhookAdapter.cs`
- Create: `src/backend/UnifiedInbox.Application/Webhooks/ReceiveWebhook/ReceiveWebhookCommand.cs`
- Create: `src/backend/UnifiedInbox.Application/Webhooks/PersistedWebhookPayload.cs`
- Create: `src/backend/UnifiedInbox.Application/Webhooks/WebhookContracts.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Storage/WebhookPayloadStore.cs`
- Create: `src/backend/UnifiedInbox.Api/Controllers/Webhooks/WhatsAppWebhookController.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Webhooks/WebhookIntakeTests.cs`

- [ ] **Step 1: Write failing webhook intake tests**

Test valid signatures return 200 after `WebhookEvent + OutboxMessage` commit, invalid signatures return 401 without persistence, duplicate `(ChannelId, ExternalEventId)` returns 200 with status `Ignored`, and a broker outage does not lose the persisted event.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/UnifiedInbox.IntegrationTests --filter WebhookIntakeTests`

Expected: FAIL because the endpoint does not exist.

- [ ] **Step 3: Define a durable adapter boundary**

```csharp
public interface IWebhookAdapter
{
    Platform Platform { get; }
    ValueTask<WebhookValidationResult> ValidateAsync(WebhookRequestSnapshot request, CancellationToken ct);
    ValueTask<IReadOnlyList<NormalizedWebhookItem>> ParseAsync(PersistedWebhookPayload payload, CancellationToken ct);
}
```

`WebhookRequestSnapshot` contains copied headers, query values, request bytes, received timestamp, and resolved public route. It must not reference ASP.NET `HttpRequest`.

- [ ] **Step 4: Implement intake transaction**

Resolve the channel from provider routing data, validate before accepting, store immutable bytes in encrypted object storage or an encrypted database column, insert the unique webhook event, and insert a `webhook.received` outbox record in one transaction.

- [ ] **Step 5: Run intake tests and commit**

```powershell
dotnet test tests/UnifiedInbox.IntegrationTests --filter WebhookIntakeTests
git add src/backend tests/UnifiedInbox.IntegrationTests
git commit -m "feat: persist validated webhook events durably"
```

Expected: all webhook intake tests pass.

## Task 7: Process inbound messages idempotently

**Files:**
- Create: `src/backend/UnifiedInbox.Application/Webhooks/ProcessWebhook/ProcessWebhookHandler.cs`
- Create: `src/backend/UnifiedInbox.Application/Contacts/ResolveContact/ContactResolver.cs`
- Create: `src/backend/UnifiedInbox.Application/Conversations/ResolveConversation/ConversationResolver.cs`
- Create: `src/backend/UnifiedInbox.Worker/Webhooks/WebhookConsumer.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Messaging/InboundMessageTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Messaging/InboundOrderingTests.cs`

- [ ] **Step 1: Write failing inbound acceptance tests**

Cover PRD acceptance criteria: normalize, resolve tenant/channel/contact/conversation, persist one message, increment unread, reopen closed conversations, preserve provider/webhook/internal timestamps, and emit a tenant-scoped real-time outbox event.

- [ ] **Step 2: Add duplicate and out-of-order tests**

Deliver the same `ExternalMessageId` through two different webhook event IDs and assert one message. Deliver later then earlier provider timestamps and assert timeline ordering while `LastMessageAt` remains the maximum.

- [ ] **Step 3: Run tests and verify failure**

Run: `dotnet test tests/UnifiedInbox.IntegrationTests --filter "InboundMessageTests|InboundOrderingTests"`

Expected: FAIL because the consumer and resolvers do not exist.

- [ ] **Step 4: Implement contact and conversation resolution**

Resolve contacts only with `(TenantId, Platform, ExternalAccountId, ExternalPlatformUserId)`. Resolve conversations with `(ChannelId, ExternalConversationId)`. Treat unique-constraint races as “load the winner,” not as processing failures.

- [ ] **Step 5: Implement one-transaction message processing**

Insert the message using `(ChannelId, ExternalMessageId)` deduplication, update conversation state, mark `WebhookEvent` processed, and append real-time/notification outbox records in one transaction. Duplicate messages mark the webhook processed/ignored without incrementing unread.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test tests/UnifiedInbox.IntegrationTests --filter "InboundMessageTests|InboundOrderingTests"
git add src/backend tests/UnifiedInbox.IntegrationTests
git commit -m "feat: process inbound messages idempotently"
```

## Task 8: Build shared inbox, activity timeline, notes, status, unread, and search APIs

**Files:**
- Create: `src/backend/UnifiedInbox.Application/Conversations/ListConversations/ListConversationsQuery.cs`
- Create: `src/backend/UnifiedInbox.Application/Conversations/GetActivity/GetConversationActivityQuery.cs`
- Create: `src/backend/UnifiedInbox.Application/Conversations/AddNote/AddInternalNoteCommand.cs`
- Create: `src/backend/UnifiedInbox.Application/Conversations/ChangeStatus/ChangeConversationStatusCommand.cs`
- Create: `src/backend/UnifiedInbox.Application/Conversations/MarkRead/MarkConversationReadCommand.cs`
- Create: `src/backend/UnifiedInbox.Application/Contacts/ListConversationHistory/ListContactConversationHistoryQuery.cs`
- Create: `src/backend/UnifiedInbox.Api/Controllers/ConversationsController.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Conversations/ConversationApiTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Conversations/ActivityTimelineTests.cs`

- [ ] **Step 1: Write failing conversation list tests**

Verify cursor pagination, customer/platform/last-message projection, and filters for `Open`, `Pending`, `Closed`, `Unread`, and all four platforms. Verify search by customer name, phone, platform handle, message content, and conversation ID.

- [ ] **Step 2: Write failing unified timeline tests**

```csharp
[Fact]
public async Task Timeline_interleaves_messages_and_notes_chronologically()
{
    await SeedInbound("first", At(10, 00));
    await SeedNote("private", At(10, 01));
    await SeedOutbound("second", At(10, 02));
    var page = await GetActivityPage();
    page.Items.Select(x => x.Kind).ShouldBe(["message", "internal_note", "message"]);
}
```

- [ ] **Step 3: Run tests and verify failure**

Run: `dotnet test tests/UnifiedInbox.IntegrationTests --filter "ConversationApiTests|ActivityTimelineTests"`

Expected: FAIL with missing routes.

- [ ] **Step 4: Implement API contracts**

Expose:

```text
GET   /api/v1/conversations
GET   /api/v1/conversations/{id}
GET   /api/v1/conversations/{id}/activity?before={cursor}&limit=50
GET   /api/v1/contacts/{id}/conversations?before={cursor}&limit=20
POST  /api/v1/conversations/{id}/notes
PATCH /api/v1/conversations/{id}/status
PUT   /api/v1/conversations/{id}/read
```

The activity cursor must sort by effective timestamp plus stable activity ID. Notes must never appear in provider-facing serialization.

- [ ] **Step 5: Make shared unread updates race-safe**

Pass the latest activity cursor seen by the client when marking read. Reset only activities at or before that cursor, so a message arriving concurrently remains unread. Test the interleaving explicitly.

- [ ] **Step 6: Add audit and outbox events**

Status changes and notes record the actor and emit `conversation.status_changed` or `internal_note.created` in the same transaction.

- [ ] **Step 7: Run tests and commit**

```powershell
dotnet test tests/UnifiedInbox.IntegrationTests --filter "ConversationApiTests|ActivityTimelineTests"
git add src/backend tests/UnifiedInbox.IntegrationTests
git commit -m "feat: add shared inbox conversation APIs"
```

## Task 9: Implement staged attachments and durable outbound sending

**Files:**
- Create: `src/backend/UnifiedInbox.Application/Attachments/StageAttachment/StageAttachmentCommand.cs`
- Create: `src/backend/UnifiedInbox.Application/Attachments/ClaimAttachments/AttachmentClaimService.cs`
- Create: `src/backend/UnifiedInbox.Application/Messages/SendMessage/SendMessageCommand.cs`
- Create: `src/backend/UnifiedInbox.Application/Channels/IChannelSender.cs`
- Create: `src/backend/UnifiedInbox.Api/Controllers/AttachmentsController.cs`
- Create: `src/backend/UnifiedInbox.Api/Controllers/MessagesController.cs`
- Create: `src/backend/UnifiedInbox.Worker/Messages/OutboundMessageConsumer.cs`
- Create: `src/backend/UnifiedInbox.Worker/Attachments/ExpiredUploadCleanupService.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Messaging/OutboundMessageTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Attachments/AttachmentFlowTests.cs`

- [ ] **Step 1: Write failing attachment tests**

Verify MIME/size allowlists, tenant and uploader ownership, single claim, expired upload rejection, signed retrieval URLs, and deletion of expired unclaimed objects.

- [ ] **Step 2: Write failing outbound idempotency tests**

Submit the same `Idempotency-Key` twice and assert the same message response and one outbound outbox record. Simulate a broker outage and assert the pending message remains recoverable.

- [ ] **Step 3: Run tests and verify failure**

Run: `dotnet test tests/UnifiedInbox.IntegrationTests --filter "OutboundMessageTests|AttachmentFlowTests"`

Expected: FAIL because upload and outbound endpoints do not exist.

- [ ] **Step 4: Implement staged upload and atomic claim**

`POST /api/v1/attachments` returns an upload ID after storing validated content. `POST /api/v1/conversations/{id}/messages` creates the pending message, claims all supplied uploads, and writes an outbound outbox event in one transaction.

- [ ] **Step 5: Implement unambiguous outbound state transitions**

Use `Pending -> Sending -> Sent/Delivered/Read` and `Pending/Sending -> Failed`. Temporary failures schedule retry. A timeout after provider submission becomes `Unknown`/reconciliation-required rather than an immediate blind retry unless the adapter declares provider idempotency support.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test tests/UnifiedInbox.IntegrationTests --filter "OutboundMessageTests|AttachmentFlowTests"
git add src/backend tests/UnifiedInbox.IntegrationTests
git commit -m "feat: add durable outbound messaging and attachments"
```

## Task 10: Add tenant-scoped SignalR delivery and notifications

**Files:**
- Create: `src/backend/UnifiedInbox.Api/Hubs/InboxHub.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/RealTime/SignalREventDispatcher.cs`
- Create: `src/backend/UnifiedInbox.Application/Notifications/NotificationService.cs`
- Create: `src/backend/UnifiedInbox.Api/Controllers/NotificationsController.cs`
- Modify: `src/backend/UnifiedInbox.Api/Program.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/RealTime/TenantSignalRTests.cs`

- [ ] **Step 1: Write failing hub isolation tests**

Connect Tenant A and Tenant B clients, publish an event for Tenant A, and assert only Tenant A receives it. Also test rejected connections without a valid access token.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/UnifiedInbox.IntegrationTests --filter TenantSignalRTests`

Expected: FAIL because `/hubs/inbox` does not exist.

- [ ] **Step 3: Implement authenticated tenant groups**

On connection, read the server-validated `tenant_id` claim and add the connection to `tenant:{tenantId}`. Never accept a tenant ID from query parameters or client invocations.

- [ ] **Step 4: Dispatch persisted real-time events**

Consume real-time outbox messages and send `message.received`, `message.sent`, `message.status_changed`, `conversation.updated`, `conversation.status_changed`, `internal_note.created`, `notification.created`, and `channel.health_changed` to the tenant group. Configure the Redis backplane.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests/UnifiedInbox.IntegrationTests --filter TenantSignalRTests
git add src/backend tests/UnifiedInbox.IntegrationTests
git commit -m "feat: add tenant-scoped realtime updates"
```

## Task 11: Build the React authentication shell and shared inbox

**Files:**
- Create: `src/frontend/src/app/router.tsx`
- Create: `src/frontend/src/app/queryClient.ts`
- Create: `src/frontend/src/auth/AuthProvider.tsx`
- Create: `src/frontend/src/auth/LoginPage.tsx`
- Create: `src/frontend/src/inbox/InboxPage.tsx`
- Create: `src/frontend/src/inbox/ConversationList.tsx`
- Create: `src/frontend/src/inbox/ConversationTimeline.tsx`
- Create: `src/frontend/src/inbox/MessageComposer.tsx`
- Create: `src/frontend/src/inbox/CustomerSidebar.tsx`
- Create: `src/frontend/src/realtime/InboxConnection.ts`
- Create: `src/frontend/src/api/client.ts`
- Create: `src/frontend/src/i18n/en.ts`
- Test: `src/frontend/src/inbox/InboxPage.test.tsx`
- Test: `src/frontend/src/inbox/MessageComposer.test.tsx`

- [ ] **Step 1: Write failing UI tests**

Use Testing Library to verify login includes workspace slug, inbox filters/search call the expected API, timeline distinguishes internal notes, canned text remains editable, the composer supplies an idempotency key, and unsupported actions are disabled from channel capabilities.

- [ ] **Step 2: Run tests and verify failure**

Run: `bun --cwd src/frontend test --run`

Expected: FAIL because the components do not exist.

- [ ] **Step 3: Implement routing, auth, and API error handling**

Create routes for `/auth/login`, `/`, `/channels`, `/team`, `/canned`, `/audit`, and `/settings`. Keep the access token in memory, rotate through an HttpOnly secure refresh cookie, and handle 401 with one refresh attempt before redirecting to login.

- [ ] **Step 4: Implement the responsive inbox from `preview.html`**

Recreate the three-column desktop layout as React components, hide the profile column on tablet, and switch between list/thread views on mobile. Include the lightweight customer profile and its prior-conversation list, emoji insertion, accessible labels, keyboard focus order, non-color status text, and PRD empty states. Put user-facing English strings in `src/frontend/src/i18n/en.ts` rather than scattering literals through components.

- [ ] **Step 5: Wire TanStack Query and SignalR**

REST loads authoritative pages. SignalR events update or invalidate conversation list, timeline, unread count, status, delivery state, and notifications. Reconnect must refetch affected queries to recover missed events. When the user opts in, use the browser Notification API for new messages and delivery/channel failures; denial must leave in-app notifications working.

- [ ] **Step 6: Run frontend tests and build**

```powershell
bun --cwd src/frontend test --run
bun --cwd src/frontend run build
```

Expected: tests pass and production build exits 0.

- [ ] **Step 7: Commit**

```powershell
git add src/frontend
git commit -m "feat: build realtime shared inbox UI"
```

## Task 12: Add team, channels, canned responses, and audit views

**Files:**
- Create: `src/backend/UnifiedInbox.Api/Controllers/UsersController.cs`
- Create: `src/backend/UnifiedInbox.Api/Controllers/ChannelsController.cs`
- Create: `src/backend/UnifiedInbox.Api/Controllers/CannedResponsesController.cs`
- Create: `src/backend/UnifiedInbox.Api/Controllers/AuditLogsController.cs`
- Create: `src/frontend/src/team/TeamPage.tsx`
- Create: `src/frontend/src/channels/ChannelsPage.tsx`
- Create: `src/frontend/src/canned/CannedResponsesPage.tsx`
- Create: `src/frontend/src/audit/AuditLogPage.tsx`
- Test: `tests/UnifiedInbox.IntegrationTests/Admin/AdminApiTests.cs`
- Test: `src/frontend/src/canned/CannedResponsePicker.test.tsx`

- [ ] **Step 1: Write failing RBAC and canned-response tests**

Verify owners/admins manage users and canned responses, agents only use canned responses, exact channel-management policies are enforced, and only owners view audit logs.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/UnifiedInbox.IntegrationTests --filter AdminApiTests`

Expected: FAIL with missing endpoints.

- [ ] **Step 3: Implement admin APIs and actor audit records**

Every user, role, channel, status, note, send, and canned-response mutation writes an audit record with tenant, actor, resource, timestamp, and non-sensitive metadata.

- [ ] **Step 4: Implement UI views and searchable canned picker**

The composer picker must open from `/`, filter title/content/shortcut, insert content without sending, and allow the agent to edit before sending.

- [ ] **Step 5: Run tests and commit**

```powershell
dotnet test tests/UnifiedInbox.IntegrationTests --filter AdminApiTests
bun --cwd src/frontend test --run
git add src/backend src/frontend tests/UnifiedInbox.IntegrationTests
git commit -m "feat: add workspace administration and canned responses"
```

## Task 13: Implement WhatsApp Cloud API end-to-end

**Files:**
- Create: `src/backend/UnifiedInbox.Infrastructure/Channels/WhatsApp/WhatsAppAdapter.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Channels/WhatsApp/WhatsAppSignatureValidator.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Channels/WhatsApp/WhatsAppPayloadParser.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Channels/WhatsApp/WhatsAppSender.cs`
- Create: `src/backend/UnifiedInbox.Domain/Channels/WhatsAppTemplate.cs`
- Create: `src/backend/UnifiedInbox.Application/Channels/WhatsApp/WhatsAppMessagingPolicy.cs`
- Test: `tests/UnifiedInbox.Application.Tests/Channels/WhatsAppMessagingPolicyTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Channels/WhatsAppContractTests.cs`
- Test fixture: `tests/UnifiedInbox.IntegrationTests/Fixtures/WhatsApp/*.json`

- [ ] **Step 1: Add sanitized webhook contract fixtures**

Include text, media, status, batched-entry, duplicate, and unsupported-event payloads. Fixtures must contain no real phone numbers, tokens, or customer content.

- [ ] **Step 2: Write failing signature, parsing, and policy tests**

Verify `X-Hub-Signature-256`, webhook verification challenge, batched payload parsing, delivery/read mapping, media metadata, 24-hour free-form eligibility, and required approved templates outside the window.

- [ ] **Step 3: Run tests and verify failure**

Run: `dotnet test --filter "WhatsAppMessagingPolicyTests|WhatsAppContractTests"`

Expected: FAIL because the adapter is not implemented.

- [ ] **Step 4: Implement capability and policy results**

Return structured results such as `AllowedFreeform`, `TemplateRequired`, `UnsupportedMedia`, `ReauthorizationRequired`, and `ProviderRateLimited`. Model supported MIME types, byte limits, status capabilities, and provider idempotency—not a single `SupportsMedia` boolean.

- [ ] **Step 5: Implement outbound templates and reconciliation**

Persist approved template name/language/components. Reject invalid free-form requests at the API boundary. Store provider request IDs, query status when a send result is ambiguous, and retry only when the adapter can prove the original was not accepted or provides an idempotency mechanism.

- [ ] **Step 6: Run tests and commit**

```powershell
dotnet test --filter "WhatsAppMessagingPolicyTests|WhatsAppContractTests"
git add src/backend tests
git commit -m "feat: integrate WhatsApp messaging end to end"
```

## Task 14: Add channel health, retries, DLQ operations, security, and observability

**Files:**
- Create: `src/backend/UnifiedInbox.Worker/Health/ChannelHealthMonitor.cs`
- Create: `src/backend/UnifiedInbox.Api/Controllers/OperationsController.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Observability/TelemetryExtensions.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Security/RateLimitingExtensions.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Security/SecretProtector.cs`
- Create: `docs/runbooks/webhook-replay.md`
- Create: `docs/runbooks/outbound-reconciliation.md`
- Create: `docs/runbooks/backup-recovery.md`
- Test: `tests/UnifiedInbox.IntegrationTests/Operations/RecoveryTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/Security/SecurityBoundaryTests.cs`

- [ ] **Step 1: Write failing operational recovery tests**

Verify exponential backoff with jitter, maximum attempts, RabbitMQ DLQ routing, authorized inspection, idempotent replay, channel transition to `ReauthorizationRequired`, and admin notifications.

- [ ] **Step 2: Write failing security boundary tests**

Verify rate limits on login/webhooks/sends/uploads, webhook replay rejection, MIME-content mismatch rejection, token redaction, signed URL expiry, and cross-tenant denial for every resource controller.

- [ ] **Step 3: Run tests and verify failure**

Run: `dotnet test tests/UnifiedInbox.IntegrationTests --filter "RecoveryTests|SecurityBoundaryTests"`

Expected: FAIL because operational endpoints and controls do not exist.

- [ ] **Step 4: Implement telemetry and health metrics**

Emit structured logs with correlation and tenant IDs but no message content or credentials. Add OpenTelemetry traces across HTTP → database → outbox → RabbitMQ → worker and metrics for webhook latency/failure, duplicate rate, outbound latency/failure, queue/DLQ depth, API latency, SignalR connections, and channel health.

- [ ] **Step 5: Define storage, retention, and recovery controls**

Document and configure database/object-storage encryption at rest, encrypted backups, retention for messages/attachments/webhook payloads/audit logs/deleted tenants, secure deletion jobs, backup monitoring, and quarterly restore tests. Use an initial MVP recovery objective of **RPO ≤ 15 minutes** and **RTO ≤ 4 hours**; changing either value requires an explicit architecture decision record and updated restore test.

- [ ] **Step 6: Write exact operator runbooks**

Each runbook must include prerequisites, read-only diagnosis, replay/reconciliation command, idempotency warning, success evidence, rollback/stop condition, and escalation path. No runbook may instruct direct production table editing.

- [ ] **Step 7: Run tests and commit**

```powershell
dotnet test tests/UnifiedInbox.IntegrationTests --filter "RecoveryTests|SecurityBoundaryTests"
git add src/backend tests docs/runbooks
git commit -m "feat: add messaging operations and security controls"
```

## Task 15: Verify the complete MVP acceptance story

**Files:**
- Create: `tests/e2e/package.json`
- Create: `tests/e2e/playwright.config.ts`
- Create: `tests/e2e/specs/shared-inbox.spec.ts`
- Create: `tests/e2e/specs/tenant-isolation.spec.ts`
- Create: `tests/e2e/specs/channel-failure.spec.ts`
- Create: `scripts/verify-mvp.ps1`
- Create: `README.md`

- [ ] **Step 1: Scaffold and write browser acceptance tests**

Run:

```powershell
New-Item -ItemType Directory -Force tests/e2e
bun --cwd tests/e2e init -y
bun --cwd tests/e2e add -d @playwright/test
bun --cwd tests/e2e x playwright install chromium
```

Cover registration/login, channel connection display, incoming WhatsApp fixture, shared real-time visibility in two staff sessions, reply delivery, sender identity, chronological note privacy, status change, closed-conversation reopening, unread behavior, search/filter, attachment send, duplicate webhook suppression, and failed-delivery visibility.

- [ ] **Step 2: Write tenant-isolation browser/API tests**

Use two tenants and assert Tenant A cannot discover Tenant B through direct URL, REST ID substitution, search, SignalR, attachment URL, notifications, or audit logs.

- [ ] **Step 3: Run the complete browser acceptance suite**

Run: `bun --cwd tests/e2e x playwright test`

Expected: all scenarios pass if the preceding slices are correctly integrated. If any fail, keep the assertion intact and record the exact failing boundary.

- [ ] **Step 4: Fix only acceptance wiring gaps**

Make minimal route, seed, configuration, or event-wiring changes required by the failing scenarios. Any domain behavior gap discovered here must receive a lower-level regression test before its fix.

- [ ] **Step 5: Add the single verification entry point**

Create `scripts/verify-mvp.ps1` that stops on failure and runs:

```powershell
dotnet format UnifiedInbox.slnx --verify-no-changes
dotnet build UnifiedInbox.slnx
dotnet test UnifiedInbox.slnx --no-build
bun --cwd src/frontend test --run
bun --cwd src/frontend run build
bun --cwd tests/e2e x playwright test
```

- [ ] **Step 6: Run full verification**

Run: `powershell -ExecutionPolicy Bypass -File scripts/verify-mvp.ps1`

Expected: formatter clean, build succeeds, all .NET tests pass, frontend tests pass, frontend production build succeeds, and all Playwright projects pass.

- [ ] **Step 7: Update project documentation**

Document prerequisites, environment variables, local startup, migrations, seeded test identities, webhook tunneling, test commands, and links to the three operator runbooks. Never include live credentials.

- [ ] **Step 8: Commit**

```powershell
git add tests/e2e scripts/verify-mvp.ps1 README.md src tests
git commit -m "test: verify unified inbox MVP acceptance criteria"
```

## Final release gate

Before calling the MVP complete, attach fresh evidence for all of the following:

- `scripts/verify-mvp.ps1` exits 0.
- A duplicate webhook produces exactly one internal message.
- A database commit followed by broker unavailability is recovered by the outbox dispatcher.
- Two users in one tenant see messages, notes, statuses, and failures in real time.
- A user from another tenant receives no REST, SignalR, notification, attachment, or audit data.
- WhatsApp free-form sending outside the service window is blocked or converted to an approved template flow.
- Backup restore and DLQ replay runbooks have been exercised in a non-production environment.
- No conversation assignment entity, route, filter, or UI affordance exists.
