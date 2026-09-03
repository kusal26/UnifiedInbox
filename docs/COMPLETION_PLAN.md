# Unified Inbox — Completion Plan

Living plan for finishing the WhatsApp-first shared inbox. Work proceeds in
dependency order; each phase is independently shippable and must leave
`dotnet build`, `dotnet test`, `bun run test`, `tsc --noEmit`, and `bun run build` green.

Decisions (refinable later):

- Attachment bytes upload **direct-to-MinIO** via presigned PUT; the API stays out of the byte path.
- Invitation emails reuse the existing SMTP / `IMailSender` path (Mailpit in Development/Test).
- Frontend migrates **screen-by-screen** alongside backend phases, not as a big-bang rewrite.
- Messenger, Instagram, TikTok, billing, subscriptions, and conversation assignment stay out of scope.
  Adapter boundaries must not complicate the WhatsApp implementation.

## Status

Done (committed as `774f76a`):

- Fail-closed EF tenant filters + `FORCE ROW LEVEL SECURITY` migration (`HardenedTenantIsolation`).
- Unscoped `ProviderRoute` table; webhook routes by `phone_number_id`, never by tenant/channel input.
- Dedicated one-shot `migrator` Compose service; API/worker never migrate; Production startup guards.
- Register returns 202 + email verification; resend/forgot/reset; session list/revoke; refresh-token
  reuse detection with family revocation; SMTP sender.
- 24h messaging-window enforcement with template escape hatch; staged-attachment claim on send.
- Attachment stage/complete endpoints with expiry, extension/MIME match, ClamAV gate.
- RFC 7807 `code` values; conversation list returns `{ items, nextCursor }` with a compat parser.

Done (this change):

- Phase A — Attachment bytes: `IObjectStorage` + MinIO presigned PUT/GET, magic-byte sniffing for all
  6 types, ClamAV INSTREAM scans, presigned downloads, `AttachmentCleanupWorker`; Compose publishes
  MinIO :9000 with separate presign endpoint; 31 new backend tests.
- Phase B — Invitations + admin API: invite/accept/revoke lifecycle, role change (Owner), activate/
  deactivate with session revocation (Admin for agents), canned CRUD, notification read + preferences,
  audit CSV export, 7/30/90-day metrics, membership re-read on sensitive ops and SignalR connect.
- Phase C — Durable messaging: 5s/30s/2m/10m jittered retries, transient/permanent classification,
  publisher confirms (RabbitMQ.Client v7 channel options), row-version claims, ambiguous-send
  reconciliation, dead-letter admin notifications, all canonical events, structured SignalR DTOs,
  `RetrySweeper` backstop.
- Phase D — WhatsApp onboarding: single-use 10-minute attempts, backend-only code exchange, scope/
  phone/WABA validation, subscribe-before-connected, `ProviderRoute` upsert, key rotation with
  previous-key fallback, test/health/toggle/reauthorize/disconnect lifecycle, `ChannelHealth` history,
  stale-webhook monitor, hardened webhook parser (statuses, media, errors, unknown, malformed).
- Phase E — Frontend: TanStack Query inbox (cursor pages, optimistic mutations with rollback),
  registration/verification/forgot/reset/invitation-accept routes, notification center, Embedded
  Signup wizard + channel repair, template + attachment uploads, live workspace metrics/team/channels/
  canned/audit/settings, role-gated navigation, targeted realtime invalidation; rewritten Playwright
  specs against the real stack.
- Phase F — Release hardening: `NOBYPASSRLS` `app_runtime` role (Compose init script + conditional
  migration grants), Docker-gated Testcontainers suites (empty-DB migrations, forced RLS, uniqueness,
  broker topology/redelivery/confirms), CI workflow (build, tests, drift, secrets, containers, empty-DB
  Compose, e2e), production checklist runbook.

Verification: `dotnet build` (0 warnings), `dotnet test` (85 passed, 5 docker-gated skipped),
`dotnet ef migrations has-pending-model-changes` (clean), `bun run test --run` (38 passed),
`tsc --noEmit` (clean), `bun run build` (ok).

## Phase A — Attachment bytes (unblocks full §3)

- `IObjectStorage` interface (presigned PUT/GET, delete) + MinIO implementation; wire into `StageAsync`.
- Magic-byte sniffing for JPEG/PNG/GIF/WebP/PDF/MP4 + size re-check on complete.
- ClamAV TCP scan on complete (fail closed outside Development/Test).
- Download via short-lived presigned GET after ownership check.
- Scheduled cleanup worker for expired staging records, unclaimed objects, orphaned keys.
- ClamAV already in Compose; add `CLAMAV_HOST` wiring to API.
- Tests: attachment state-transition unit tests; Testcontainers MinIO ownership test; ClamAV
  rejection test (EICAR); cross-tenant claim/download denial.
- Accept: 10 MB + 6-type matrix enforced end-to-end; expired staging cleaned.

## Phase B — Invitations and admin API (finishes §2 + §6 backend)

- Invitation lifecycle: `POST /api/v1/invitations` (Owner/Admin, 72h hashed token + mail),
  `POST /api/v1/invitations/accept` (creates verified user), `DELETE /api/v1/invitations/{id}` (revoke).
- User lifecycle: role change (Owner only), deactivate/reactivate; re-read membership and active
  status on sensitive operations and SignalR connect.
- Admin gaps: canned-response update/delete, notification read + preferences, audit CSV export,
  7/30/90-day overview metrics endpoint.
- Audit records for auth changes, invitations, role changes, session revocations, auth failures.
- Tests: every endpoint × Owner/Admin/Agent, validation, RFC 7807 codes, expiry, cross-tenant denial.
- Accept: full invite → accept → login flow works via Mailpit in Development.

## Phase C — Durable messaging (finishes §5)

- Retry queues (5s / 30s / 2m / 10m) with jitter in `OutboxDispatcher` / `MessagingConsumer`.
- Classify transient (429/5xx/timeout) vs permanent; exhausted failures → dead-letter records +
  administrator notifications.
- Publisher confirms on outbox publish; consumers stay idempotent (DB constraints).
- Reconcile ambiguous provider sends by idempotency key / provider request ID instead of resending.
- Publish canonical events: `conversation.created`, `conversation.updated`, `message.created`,
  `message.statusChanged`, `note.created`, `channel.updated`, `notification.created`.
- SignalR payloads as structured DTOs (not serialized strings); verify Redis fan-out across API
  instances; targeted cache refresh on reconnect.
- Tests: RabbitMQ replay/retry/dead-letter Testcontainers test; duplicate delivery yields one message.
- Accept: kill-and-replay of broker traffic loses nothing and duplicates nothing.

## Phase D — WhatsApp onboarding (finishes §4)

- Embedded Signup v4: single-use 10-minute connection attempts (hashed state/nonce,
  initiating-user binding); backend-only authorization-code exchange.
- Validate WABA, phone-number ID, granted scopes, phone status via configured Graph API version.
- Subscribe WABA webhook before marking channel connected; upsert `ProviderRoute` on connect.
- Credential key rotation for versioned AES-256-GCM envelopes.
- Channel test, health, feature-toggle, reauthorization, disconnect (unsubscribe/revoke provider
  access where supported, delete ciphertext, retain history).
- Persist `ChannelHealth` history (table exists, currently unwritten); admin notifications for
  revoked access, stale webhooks, subscription failures, repeated send failures.
- Tests: contract fixtures for verification, malformed signatures, messages, media, statuses,
  errors, duplicates, unknown events, provider timeouts.
- Accept: staging WhatsApp test number connects, receives, and sends through the UI.

## Phase E — Frontend (finishes §6 UI)

- Convert inbox `useEffect` loading to TanStack Query (already a dependency): cursor pagination,
  optimistic send/note/status mutations with rollback, targeted SignalR cache updates.
- Rebuild remaining screens as feature components with queries/mutations.
- New routes: registration, email verification, forgot/reset password, invitation acceptance,
  notification center, Embedded Signup wizard, channel repair, template selection, attachment upload.
- Replace hard-coded workspace names, unread counts, canned responses, fake metrics, and
  "attachments unavailable" behavior with live data.
- Hide unauthorized navigation/actions; server-side authorization stays the security boundary.
- Preserve prototype typography, spacing, colors, responsive behavior, accessibility;
  `preview.html` remains reference-only.
- Tests: extend Vitest suites per screen; Playwright covers registration → verification → login,
  invitations, every workspace route, inbound-message arrival.
- Accept: all routes work against the real API with no mock data.

## Phase F — Release hardening

- Testcontainers: empty-database migrations, forced RLS with the real application role, uniqueness.
- `NOBYPASSRLS` application role in Compose; API/worker connect as that role.
- CI gates: restore, warning-free build, unit/integration/API/frontend tests, TypeScript,
  production bundles, migration drift (`has-pending-model-changes`), secret scan, container build,
  empty-database Compose startup.
- Production checklist: staging test number, valid Embedded Signup config, webhook delivery proof,
  backup/restore rehearsal, fake-provider rejection proof, documented rollback.

## Suggested order

A → B → C → D → E → F. Phases A–C need no Meta credentials and can proceed immediately.
