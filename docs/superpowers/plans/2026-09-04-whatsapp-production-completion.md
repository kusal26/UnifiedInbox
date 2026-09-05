# WhatsApp Production Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the verified production gaps so registration, authentication, tenant-isolated background work, WhatsApp text/template/media messaging, administration, and realtime behavior work end-to-end with the `NOBYPASSRLS` runtime role.

**Architecture:** Keep `Tenants` and `ProviderRoutes` as the only generally queryable routing data. Every operation resolves a tenant first, then executes tenant-table work inside a transaction-scoped PostgreSQL `app.current_tenant`; workers enumerate unscoped tenant IDs or use the trusted tenant header emitted by the outbox. Model WhatsApp delivery as message parts so text/templates and multiple attachments have independent provider IDs, retry state, and status reconciliation while remaining one inbox timeline item.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core/PostgreSQL forced RLS, RabbitMQ.Client 7, Redis/SignalR, MinIO, ClamAV, React 19, TanStack Query, Vitest, Playwright, Testcontainers.

---

## Delivery order and gates

Do not begin a later phase until the preceding phase's targeted tests pass. Tasks 1-3 restore the production data path; Tasks 4-6 complete WhatsApp; Tasks 7-9 close authorization, UI, and automated acceptance; Task 10 is the external release gate.

### Task 1: Introduce mandatory tenant execution scopes

**Files:**
- Create: `src/backend/UnifiedInbox.Application/Tenancy/ITenantExecutionScope.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Persistence/TenantExecutionScope.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Persistence/TenantToken.cs`
- Modify: `src/backend/UnifiedInbox.Api/Security/CurrentRequestContext.cs`
- Modify: `src/backend/UnifiedInbox.Api/Program.cs`
- Modify: `src/backend/UnifiedInbox.Worker/Program.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Persistence/TenantSessionInterceptor.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Services/AuthenticationService.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Services/InvitationService.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Services/AdministrationService.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Messaging/MessageProcessor.cs`
- Modify: `src/backend/UnifiedInbox.Worker/OutboxDispatcher.cs`
- Modify: `src/backend/UnifiedInbox.Worker/MessagingConsumer.cs`
- Modify: `src/backend/UnifiedInbox.Worker/RetrySweeper.cs`
- Modify: `src/backend/UnifiedInbox.Worker/AttachmentCleanupWorker.cs`
- Modify: `src/backend/UnifiedInbox.Worker/ChannelHealthMonitor.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/TenantExecutionScopeTests.cs`
- Test: `tests/UnifiedInbox.Api.Tests/RuntimeRoleAuthenticationTests.cs`
- Test: `tests/UnifiedInbox.Api.Tests/RuntimeRoleWebhookTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/RuntimeRoleWorkerTests.cs`

- [x] **Step 1: Add failing PostgreSQL tests for tenant-scoped execution**

  Test these exact cases with the real `app_runtime` role: no context reads zero `Users`; tenant A cannot read tenant B; a scoped transaction can read and write only tenant A; disposal resets the connection; nested attempts for another tenant throw.

  ```csharp
  await scope.RunAsync(tenantA, async token =>
  {
      (await db.Users.CountAsync(token)).ShouldBe(1);
      db.Users.Add(UserFixture.ForTenant(tenantB));
      await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync(token));
  }, CancellationToken.None);
  ```

- [x] **Step 2: Run the targeted test and confirm the current runtime role fails**

  ```powershell
  $env:RUN_DOCKER_TESTS='true'
  dotnet test tests/UnifiedInbox.IntegrationTests --filter FullyQualifiedName~TenantExecutionScopeTests
  ```

  Expected: failure because `IgnoreQueryFilters()` cannot bypass forced RLS and no transaction-local tenant context exists.

- [x] **Step 3: Implement the execution-scope contract**

  The application boundary must be explicit and non-null:

  ```csharp
  public interface ITenantExecutionScope
  {
      Guid? CurrentTenantId { get; }
      Task RunAsync(Guid tenantId, Func<CancellationToken, Task> action, CancellationToken token);
      Task<T> RunAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> action, CancellationToken token);
  }
  ```

  `TenantExecutionScope` must open an EF transaction, execute `select set_config('app.current_tenant', @tenant, true)`, set an `AsyncLocal<Guid?>`, run the action, commit, and clear the value in `finally`. It must reject `Guid.Empty` and cross-tenant nesting. Do not use connection-wide `SET`.

- [x] **Step 4: Route every bootstrap operation before touching tenant tables**

  - Login: resolve `Tenant.Id` from the normalized slug, then run the user/password/token work in that tenant scope.
  - Registration: allocate `Tenant.Id`, insert the unscoped tenant, then create Owner, verification token, and audit entry inside the same transaction and tenant scope.
  - Refresh, verification, reset, and invitation tokens: issue `v1.<tenant-id-base64url>.<random>` tokens, parse only the tenant-routing segment, enter that scope, then compare the hash of the complete token. Invalid formats return the same generic response as unknown tokens.
  - Webhooks: resolve `ProviderRoute`, enter its tenant scope, then load the channel and create receipt/outbox rows.
  - Rabbit consumers: require the signed-by-process `tenant-id` message header, compare it with the persisted entity tenant, and reject mismatches.
  - Sweepers/dispatchers/cleanup/health: enumerate `Tenants.Id`, then execute one bounded batch per tenant.

- [x] **Step 5: Remove tenant-table `IgnoreQueryFilters()` from runtime services**

  Permit it only in schema-owner migration/test utilities. Add an architecture test that fails when `IgnoreQueryFilters` appears under `UnifiedInbox.Api`, `UnifiedInbox.Worker`, or production service/messaging files.

- [x] **Step 6: Add real API/worker runtime-role tests**

  Verify register -> verify -> login, refresh rotation/reuse, webhook receipt creation, outbox dispatch, outbound consumer processing, attachment cleanup, and channel-health monitoring against PostgreSQL using `app_runtime`.

- [x] **Step 7: Run and commit**

  ```powershell
  dotnet test tests/UnifiedInbox.IntegrationTests --filter "FullyQualifiedName~TenantExecutionScopeTests|FullyQualifiedName~RuntimeRoleWorkerTests"
  dotnet test tests/UnifiedInbox.Api.Tests --filter "FullyQualifiedName~RuntimeRole"
  git add src/backend tests
  git commit -m "fix(tenancy): execute all runtime work inside forced-RLS scopes"
  ```

### Task 2: Add tenant-aware relational constraints

**Files:**
- Modify: `src/backend/UnifiedInbox.Infrastructure/Persistence/InboxDbContext.cs`
- Modify: focused entity files created while splitting `src/backend/UnifiedInbox.Domain/InboxModel.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Persistence/Migrations/<timestamp>_TenantAwareForeignKeys.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/TenantForeignKeyTests.cs`

- [x] **Step 1: Write failing PostgreSQL tests for cross-tenant references**

  Attempt cross-tenant inserts for channel credential -> channel, conversation -> channel/contact, message -> channel/conversation/sender, note -> conversation/author, attachment -> uploader/message, health -> channel, notification preference -> user, refresh token -> user, invitation -> inviter, and connection attempt -> user/channel. Every insert must fail with `23503`.

- [x] **Step 2: Define composite principal keys and foreign keys**

  Use `(TenantId, Id)` alternate keys on tenant-scoped principals and include `TenantId` in every dependent FK. Preserve existing public GUID IDs.

  ```csharp
  modelBuilder.Entity<Conversation>()
      .HasOne<Channel>().WithMany()
      .HasForeignKey(x => new { x.TenantId, x.ChannelId })
      .HasPrincipalKey(x => new { x.TenantId, x.Id })
      .OnDelete(DeleteBehavior.Restrict);
  ```

- [x] **Step 3: Split the domain model by feature while preserving namespaces**

  Create `Domain/Identity.cs`, `Domain/Channels.cs`, `Domain/Conversations.cs`, `Domain/Attachments.cs`, `Domain/Administration.cs`, and `Domain/MessagingInfrastructure.cs`. Move types without changing serialized enum values or table names.

- [x] **Step 4: Generate an additive migration and validate empty plus upgraded databases**

  ```powershell
  dotnet ef migrations add TenantAwareForeignKeys --project src/backend/UnifiedInbox.Infrastructure --startup-project src/backend/UnifiedInbox.Api
  $env:RUN_DOCKER_TESTS='true'
  dotnet test tests/UnifiedInbox.IntegrationTests --filter FullyQualifiedName~TenantForeignKeyTests
  dotnet ef migrations has-pending-model-changes --project src/backend/UnifiedInbox.Infrastructure --startup-project src/backend/UnifiedInbox.Api
  ```

- [x] **Step 5: Commit**

  ```powershell
  git add src/backend tests/UnifiedInbox.IntegrationTests
  git commit -m "fix(persistence): enforce tenant-aware foreign keys"
  ```

### Task 3: Make attachment completion and claiming safe

**Files:**
- Modify: `src/backend/UnifiedInbox.Domain/Attachments.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Services/AttachmentService.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Services/PersistentInboxService.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Persistence/Migrations/<timestamp>_AttachmentReadyState.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/AttachmentServiceTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/AttachmentClaimTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/MinioAttachmentTests.cs`

- [x] **Step 1: Add failing state-machine tests**

  Assert `Staged -> Ready -> Claimed`; reject `Staged -> Claimed`, double completion, expired completion, cross-user completion, cross-tenant claim, duplicate attachment IDs, reuse after claim, and download of uncompleted staging bytes.

- [x] **Step 2: Add `Ready` and completion metadata**

  ```csharp
  public enum AttachmentStatus { Staged, Ready, Claimed, Expired, Rejected }
  public DateTimeOffset? CompletedAt { get; set; }
  public string? DetectedContentType { get; set; }
  ```

  After length, magic-byte, extension, and ClamAV checks succeed, set `DetectedContentType`, `CompletedAt`, and `Status = Ready` in one save.

- [x] **Step 3: Make claims atomic**

  Require all distinct IDs to be `Ready`, unexpired, owned by the current tenant, and unclaimed. Use a transaction and concurrency token so simultaneous sends yield one success and one `attachment_already_claimed` response.

- [x] **Step 4: Add real MinIO and ClamAV coverage**

  Use Testcontainers to PUT valid bytes through the presigned URL, complete, claim, and download. Upload EICAR through MinIO and confirm ClamAV rejects it and the object is deleted.

- [x] **Step 5: Run and commit**

  ```powershell
  dotnet test tests/UnifiedInbox.IntegrationTests --filter "FullyQualifiedName~Attachment"
  git add src/backend tests/UnifiedInbox.IntegrationTests
  git commit -m "fix(attachments): require scanned ready uploads before claim"
  ```

### Task 4: Model durable WhatsApp message parts

**Files:**
- Create: `src/backend/UnifiedInbox.Domain/MessageDeliveryPart.cs`
- Create: `src/backend/UnifiedInbox.Application/Messaging/OutboundMessageCommand.cs`
- Modify: `src/backend/UnifiedInbox.Domain/Conversations.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Persistence/InboxDbContext.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Services/PersistentInboxService.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Messaging/MessageProcessor.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Persistence/Migrations/<timestamp>_MessageDeliveryParts.cs`
- Test: `tests/UnifiedInbox.Application.Tests/OutboundMessageCommandTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/MessageDeliveryPartTests.cs`

- [x] **Step 1: Write failing tests for text, template, and multi-attachment messages**

  A free-form message inside the window creates one text part. An approved template outside the window creates one template part. A body plus two attachments creates three ordered parts. Parent status is `Sent` only after every required part succeeds and `Failed` if a permanent part failure occurs.

- [x] **Step 2: Add explicit outbound types**

  ```csharp
  public enum DeliveryPartKind { Text, Template, Image, Video, Document }
  public sealed class MessageDeliveryPart : ITenantScoped
  {
      public Guid Id { get; set; }
      public Guid TenantId { get; set; }
      public Guid MessageId { get; set; }
      public int Position { get; set; }
      public DeliveryPartKind Kind { get; set; }
      public Guid? AttachmentId { get; set; }
      public string? TemplateName { get; set; }
      public string? TemplateLanguage { get; set; }
      public string? ExternalMessageId { get; set; }
      public MessageStatus Status { get; set; }
      public int Attempts { get; set; }
      public DateTimeOffset? NextAttemptAt { get; set; }
      public string? ProviderRequestId { get; set; }
  }
  ```

- [x] **Step 3: Persist the requested send shape**

  Replace the loose `templateName` parameter with a request containing `Body`, optional approved template identity/language/components, attachment IDs, and idempotency key. Never treat a non-empty name as proof of approval.

- [x] **Step 4: Process and reconcile each delivery part idempotently**

  Claim parts with row-version concurrency, persist a provider request ID before the HTTP call, store each provider message ID, and aggregate part status onto the parent message. Status webhooks must resolve either a part or legacy parent provider ID.

- [x] **Step 5: Run migration, tests, and commit**

  ```powershell
  dotnet test tests/UnifiedInbox.Application.Tests --filter FullyQualifiedName~OutboundMessageCommand
  dotnet test tests/UnifiedInbox.IntegrationTests --filter FullyQualifiedName~MessageDeliveryPart
  git add src/backend tests
  git commit -m "feat(messaging): add durable multipart WhatsApp delivery"
  ```

### Task 5: Implement approved templates and outbound media

**Files:**
- Modify: `src/backend/UnifiedInbox.Application/ChannelContracts.cs`
- Create: `src/backend/UnifiedInbox.Application/Messaging/WhatsAppSendPayload.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Channels/WhatsApp/WhatsAppGraphClient.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Channels/WhatsApp/WhatsAppMessageSender.cs`
- Modify: `src/backend/UnifiedInbox.Api/Controllers/ChannelsController.cs`
- Modify: `src/backend/UnifiedInbox.Api/Controllers/ConversationsController.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/WhatsAppTemplateContractTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/WhatsAppOutboundMediaContractTests.cs`

- [x] **Step 1: Add HTTP contract tests**

  Verify Graph payloads for text, `type=template` with language/components, image, MP4 video, and PDF document. Verify 401/403 -> `channel_authorization_expired`, 429 -> `provider_rate_limited`, other 5xx -> transient provider failure.

- [x] **Step 2: Add approved-template discovery**

  Add `GET /api/v1/channels/{id}/templates`, call `/{waba-id}/message_templates?status=APPROVED`, return only name, language, category, parameter schema, and approval status. Never return tokens or raw Graph responses.

- [x] **Step 3: Validate templates before command acceptance**

  Outside the 24-hour window, reject missing, unapproved, or incorrectly parameterized templates with `messaging_window_closed` or `template_invalid`. Persist the exact approved template snapshot used for delivery.

- [x] **Step 4: Upload outbound media to Graph and send by media ID**

  Read only claimed, clean MinIO objects. Upload them through `/{phone-number-id}/media`, then send the returned media ID. Keep provider media IDs on delivery parts, not attachment API DTOs.

- [x] **Step 5: Run and commit**

  ```powershell
  dotnet test tests/UnifiedInbox.IntegrationTests --filter "FullyQualifiedName~WhatsAppTemplate|FullyQualifiedName~WhatsAppOutboundMedia"
  git add src/backend tests/UnifiedInbox.IntegrationTests
  git commit -m "feat(whatsapp): send approved templates and outbound media"
  ```

### Task 6: Download, scan, and persist inbound WhatsApp media

**Files:**
- Modify: `src/backend/UnifiedInbox.Infrastructure/Channels/WhatsApp/WhatsAppPayloadParser.cs`
- Modify: `src/backend/UnifiedInbox.Application/ChannelContracts.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Channels/WhatsApp/WhatsAppGraphClient.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Channels/WhatsApp/InboundMediaIngestor.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Messaging/MessageProcessor.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/WhatsAppWebhookContractTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/InboundMediaIngestionTests.cs`

- [x] **Step 1: Extend fixtures and write failing tests**

  Cover image/video/document media IDs, captions, unsupported audio/stickers, MIME spoofing, oversize files, malware, Graph timeouts, replay after partial object upload, and duplicate webhook delivery.

- [x] **Step 2: Normalize the provider media identity**

  ```csharp
  public sealed record WhatsAppInbound(
      string ExternalMessageId,
      string CustomerId,
      string? Text,
      string? MediaId,
      string? DeclaredMimeType,
      string? FileName,
      WhatsAppInboundKind Kind);
  ```

- [x] **Step 3: Implement authenticated download and private ingestion**

  Fetch media metadata with the channel token, stream from the returned Graph URL, enforce 10 MB while streaming, sniff bytes, scan with ClamAV, and upload to a tenant-scoped MinIO key. Create a `Claimed` inbound attachment linked to the inbound message only after the object and database transaction succeed.

- [x] **Step 4: Make retries safe**

  Derive the object key from tenant/channel/external-message/media IDs. On retry, reuse a clean matching object or replace a partial object. Database uniqueness on `(TenantId, MessageId, ProviderMediaId)` must prevent duplicate attachment rows.

- [x] **Step 5: Run and commit**

  ```powershell
  dotnet test tests/UnifiedInbox.IntegrationTests --filter "FullyQualifiedName~InboundMedia|FullyQualifiedName~WhatsAppWebhook"
  git add src/backend tests/UnifiedInbox.IntegrationTests
  git commit -m "feat(whatsapp): ingest inbound media privately"
  ```

### Task 7: Complete Embedded Signup, credential lifecycle, and error contracts

**Files:**
- Modify: `src/backend/UnifiedInbox.Domain/Channels.cs`
- Modify: `src/backend/UnifiedInbox.Application/ChannelContracts.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Services/ChannelService.cs`
- Modify: `src/backend/UnifiedInbox.Infrastructure/Channels/WhatsApp/WhatsAppGraphClient.cs`
- Modify: `src/backend/UnifiedInbox.Api/Controllers/ChannelsController.cs`
- Modify: `src/backend/UnifiedInbox.Api/ProblemExceptionHandler.cs`
- Modify: `src/backend/UnifiedInbox.Api/Program.cs`
- Modify: `src/backend/UnifiedInbox.Worker/Program.cs`
- Modify: `docker-compose.yml`
- Create: `tests/UnifiedInbox.Api.Tests/AuthorizationAuditTests.cs`
- Create: `tests/UnifiedInbox.Api.Tests/ProductionConfigurationTests.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/ChannelServiceTests.cs`

- [x] **Step 1: Add connection-attempt nonce and provider configuration**

  Persist hashes for independent state and nonce values, bind both to tenant/user/purpose/channel, and expire after ten minutes. Return public App ID, Embedded Signup configuration ID, Graph version, state, nonce, and expiry; do not return secrets.

- [x] **Step 2: Verify WABA ownership**

  Query the supplied WABA's phone-number collection and require the returned phone ID to be present, verified, and accessible with both required scopes before subscribing the WABA.

- [x] **Step 3: Complete credential lifecycle**

  Populate versioned AES-GCM envelopes for access token and webhook-secret material, rotate both with previous-key fallback, remove both on disconnect, and revoke provider access where Graph supports it. Map duplicate provider routes to `asset_already_connected` without exposing the owning tenant.

- [x] **Step 4: Standardize RFC 7807 errors**

  Ensure all controller-generated errors include `traceId` and stable `code`. Include at minimum `messaging_window_closed`, `channel_authorization_expired`, `asset_already_connected`, `provider_rate_limited`, `malicious_attachment`, and `token_reuse_detected`.

- [x] **Step 5: Audit authorization failures**

  Add an authorization-result handler that records tenant, actor, policy, method, and normalized route without request bodies or secrets. Re-read active membership for every sensitive command and SignalR connection.

- [x] **Step 6: Enforce production configuration in API and worker**

  Both processes must reject fake-provider mode, missing App ID/config ID/app secret/verify token, invalid credential keys, and weak JWT keys in Production. Move validation into a shared infrastructure validator with direct unit tests.

- [x] **Step 7: Run and commit**

  ```powershell
  dotnet test tests/UnifiedInbox.Api.Tests --filter "FullyQualifiedName~AuthorizationAudit|FullyQualifiedName~ProductionConfiguration"
  dotnet test tests/UnifiedInbox.IntegrationTests --filter FullyQualifiedName~ChannelService
  git add src/backend tests docker-compose.yml
  git commit -m "feat(channels): finish secure Embedded Signup lifecycle"
  ```

### Task 8: Add broker retry queues and verify realtime fan-out

**Files:**
- Create: `src/backend/UnifiedInbox.Infrastructure/Messaging/RabbitMqTopology.cs`
- Create: `src/backend/UnifiedInbox.Infrastructure/Messaging/RetryEnvelope.cs`
- Modify: `src/backend/UnifiedInbox.Worker/OutboxDispatcher.cs`
- Modify: `src/backend/UnifiedInbox.Worker/MessagingConsumer.cs`
- Modify: `src/backend/UnifiedInbox.Worker/RetrySweeper.cs`
- Modify: `src/backend/UnifiedInbox.Api/Hubs/RealtimeSubscriber.cs`
- Test: `tests/UnifiedInbox.IntegrationTests/RabbitMqRetryTests.cs`
- Create: `tests/UnifiedInbox.IntegrationTests/RedisSignalRTests.cs`

- [x] **Step 1: Add failing broker tests**

  Verify transient deliveries traverse 5-second, 30-second, 2-minute, and 10-minute durable TTL queues with jitter buckets; publisher confirms precede database acknowledgement; permanent/exhausted messages reach a durable dead-letter queue and produce one administrator notification.

- [x] **Step 2: Centralize topology declaration**

  Declare one topic exchange, worker/realtime queues, four retry queues with dead-letter routing back to the worker exchange, and a terminal dead-letter queue. API, worker, and tests must call the same topology helper.

- [x] **Step 3: Publish retries with confirms**

  When processing returns a retry result, publish a persistent retry envelope containing tenant ID, entity ID, operation, attempt, and not-before time. Acknowledge the original delivery only after broker confirmation and persisted retry state.

- [x] **Step 4: Verify two-API Redis behavior**

  Start two API instances against one Redis and RabbitMQ. Connect a SignalR client to each, publish one canonical event, and assert each tenant client sees it once while a second tenant sees nothing. Reconnect and assert targeted REST cache refresh succeeds.

- [x] **Step 5: Run and commit**

  ```powershell
  $env:RUN_DOCKER_TESTS='true'
  dotnet test tests/UnifiedInbox.IntegrationTests --filter "FullyQualifiedName~RabbitMqRetry|FullyQualifiedName~RedisSignalR"
  git add src/backend tests/UnifiedInbox.IntegrationTests
  git commit -m "feat(messaging): add durable retry queues and realtime verification"
  ```

### Task 9: Finish production frontend flows and HTTP acceptance tests

**Files:**
- Create: `src/frontend/src/channels/EmbeddedSignupButton.tsx`
- Create: `src/frontend/src/channels/ChannelRepairPage.tsx`
- Create: `src/frontend/src/inbox/TemplatePicker.tsx`
- Create: `src/frontend/src/inbox/AttachmentComposer.tsx`
- Modify: `src/frontend/src/workspace/WorkspacePages.tsx`
- Modify: `src/frontend/src/inbox/InboxPage.tsx`
- Modify: `src/frontend/src/api/admin.ts`
- Modify: `src/frontend/src/api/inbox.ts`
- Modify: `src/frontend/src/app/App.tsx`
- Modify: `src/frontend/src/app/routes.ts`
- Create: `tests/UnifiedInbox.Api.Tests/UnifiedInbox.Api.Tests.csproj`
- Create: `tests/UnifiedInbox.Api.Tests/ApiFactory.cs`
- Create: `tests/UnifiedInbox.Api.Tests/AuthApiTests.cs`
- Create: `tests/UnifiedInbox.Api.Tests/AttachmentApiTests.cs`
- Create: `tests/UnifiedInbox.Api.Tests/ChannelApiTests.cs`
- Create: `tests/UnifiedInbox.Api.Tests/ConversationApiTests.cs`
- Create: `tests/UnifiedInbox.Api.Tests/AdministrationApiTests.cs`
- Modify: `tests/e2e/specs/auth.spec.ts`
- Modify: `tests/e2e/specs/invitations.spec.ts`
- Modify: `tests/e2e/specs/workspace-routes.spec.ts`
- Create: `tests/e2e/specs/messaging.spec.ts`
- Create: `tests/e2e/specs/channels.spec.ts`
- Create: `tests/e2e/specs/administration.spec.ts`

- [x] **Step 1: Replace manual onboarding with the Meta SDK**

  Load the SDK from Meta only on the channel connect/repair routes, launch Embedded Signup with backend-provided App/config IDs and nonce, accept the code/session payload through `postMessage` with strict origin validation, and submit it to the backend. Remove manual authorization-code, phone-ID, and business-ID text fields.

- [x] **Step 2: Add approved-template selection and attachment delivery state**

  When the window is closed, require `TemplatePicker`; render parameters from the approved schema. Show upload/scanning/ready/claimed states and block Send until every selected attachment is Ready. Display provider delivery-part failures without losing the composed text.

- [x] **Step 3: Add complete API authorization matrices**

  For every endpoint, test unauthenticated, inactive user, Agent, Admin, Owner, malformed input, cross-tenant resource ID, and rate-limit behavior as applicable. Assert status, content type, stable problem code, and absence of secrets/cross-tenant metadata.

- [x] **Step 4: Expand Playwright from route smoke tests to behavior tests**

  Cover registration/verification/login, invitation/revocation/acceptance, sessions, inbound text/media, free-form replies, outside-window templates, multiple attachments, notes, read/status changes, channel connect/test/repair/toggle/disconnect, notification preferences/read state, canned CRUD, metrics ranges, user role/activity, audit filtering/export, and workspace persistence.

- [x] **Step 5: Run and commit**

  ```powershell
  dotnet test tests/UnifiedInbox.Api.Tests
  Push-Location src/frontend
  bun run test --run
  bunx tsc --noEmit
  bun run build
  Pop-Location
  bunx --cwd tests/e2e playwright test
  git add src/frontend tests
  git commit -m "feat(frontend): complete WhatsApp onboarding and messaging flows"
  ```

### Task 10: Close CI, documentation, and production acceptance

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `scripts/verify-mvp.ps1`
- Modify: `README.md`
- Modify: `docs/COMPLETION_PLAN.md`
- Modify: `docs/runbooks/production-checklist.md`
- Create: `docs/releases/whatsapp-production-acceptance.md`

- [ ] **Step 1: Make Docker-backed suites mandatory in CI**

  Fail rather than skip PostgreSQL runtime-role, RabbitMQ retry, Redis SignalR, MinIO, and ClamAV tests. Boot Compose from an empty volume, wait for migrator completion, then execute register -> verify -> login and webhook -> worker -> inbox smoke requests.

- [ ] **Step 2: Add migration and configuration gates**

  Keep warning-as-error build, pending-model check, frontend checks, secret scan, image builds, and E2E. Add explicit Production boot-negative tests for every forbidden/missing configuration and a positive boot test with syntactically valid secrets.

- [ ] **Step 3: Correct repository documentation**

  Update README to say only the migrator applies migrations. Change `docs/COMPLETION_PLAN.md` from “done” to evidence-linked status until acceptance is recorded. Clarify that `CHANNEL_FRONTEND_BACKEND_INTEGRATION.md` is future-channel reference only.

- [ ] **Step 4: Run the complete local gate**

  ```powershell
  $env:RUN_DOCKER_TESTS='true'
  ./scripts/verify-mvp.ps1
  docker compose config --quiet
  docker compose down -v
  docker compose up -d --build
  bunx --cwd tests/e2e playwright test
  ```

  Expected: zero failed or skipped required tests, clean migration drift, healthy Compose services, and all Playwright scenarios passing.

- [ ] **Step 5: Perform staging acceptance**

  With a real Meta staging number: complete Embedded Signup; prove duplicate webhook delivery creates one receipt/message; exchange inbound text and each supported media type; reply with free-form text, template, and attachments; revoke and repair access; confirm health/notification/audit events; rehearse backup and restore; record RTO/RPO; prove fake mode is rejected; record image tags and rollback steps.

- [ ] **Step 6: Record evidence and commit**

  `docs/releases/whatsapp-production-acceptance.md` must contain dates, environment identifiers without secrets, command/test summaries, webhook/provider request IDs, backup/restore timings, image digests, rollback tag, approver, and unresolved non-blocking observations.

  ```powershell
  git add .github scripts README.md docs
  git commit -m "chore(release): complete WhatsApp production acceptance gates"
  ```

## Definition of done

- The API and worker use `app_runtime`; schema migration uses only the owner connection.
- No runtime code bypasses tenant query filters or forced RLS.
- Registration, verification, login, refresh, invitations, webhooks, and workers pass against the real runtime role.
- Only scanned `Ready` attachments can be claimed.
- Inbound and outbound supported media are private, scanned, tenant-isolated, and visible in the inbox.
- Approved templates send successfully outside the 24-hour window; unapproved free-form sends are rejected before persistence.
- Every provider send part is idempotent and reconcilable.
- Embedded Signup uses Meta's SDK and verifies scopes, WABA ownership, phone status, state, nonce, and initiating user.
- All stable problem codes, RBAC matrices, authorization audits, retry/DLQ behavior, and canonical realtime events are covered by automated tests.
- CI has no skipped required suites, and the staging acceptance record is complete.
