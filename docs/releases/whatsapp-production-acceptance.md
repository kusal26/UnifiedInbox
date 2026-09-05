# WhatsApp Production Acceptance

Status: **PENDING — awaiting staging acceptance against a real Meta test number.**

This file is the evidence record for the final external gate of the WhatsApp production
completion plan (Task 10, Steps 5–6). Each section maps to `docs/runbooks/production-checklist.md`.
Nothing below is "done" until it has a date, environment identifier (no secrets), and a
command/test summary. Fill each row, then flip the status at the top and record the commit.

## Environment identifiers

| Item | Value |
| --- | --- |
| API image digest |  |
| Worker image digest |  |
| Frontend image digest |  |
| Rollback image tag (previous triple) |  |
| Staging environment identifier |  |
| Date(s) of acceptance run |  |
| Approver |  |

## 1. Staging test number

- Embedded Signup wizard connected the staging number; channel row shows `connected`.
- Exactly one `ProviderRoute` exists for its `phone_number_id`; `channel.updated` event seen.
- Inbound from the test handset and an inbox reply both appear in the timeline.
- Evidence (dates / webhook or provider request IDs / commands):

## 2. Embedded Signup configuration

- `WhatsApp:AppId`, `WhatsApp:AppSecret`, `WhatsApp:GraphVersion` set from the secret store.
- Meta app has `whatsapp_business_messaging` and `whatsapp_business_management` approved.
- A fresh connect attempt completes without `scopes_missing`; code exchange never logs the secret.
- Evidence:

## 3. Webhook delivery proof

- WABA subscribed to `messages`; API answers 200; a `WebhookReceipt` reaches `Processed`.
- Replaying the same payload twice creates exactly one inbound message.
- Realtime event arrived in an open browser session.
- Evidence (webhook request IDs):

## 4. Backup and restore rehearsal

- Postgres base backup + WAL archives taken; restored into an empty staging database.
- One-shot `migrator` run; `/ready` passes; spot-checked conversation renders.
- Measured RTO / RPO:
- Evidence:

## 5. Fake-provider rejection proof

- `ProductionGuard.Validate` (run as `ProductionConfigurationTests` in CI `production-guards`)
  rejects `WHATSAPP_USE_FAKE=true`, missing `AppSecret`/`VerifyToken`/`Credentials:MasterKey`
  (32-byte base64) and weak `Jwt:SigningKey`; positive boot with syntactically valid secrets.
- Evidence (test/command output):

## 6. Rollback

- API/worker/frontend tagged as one triple; rollback = previous tag triple + re-run `migrator`.
- Migrations additive; destructive changes ship separately; DB restores per `backup-recovery.md`;
  never roll the DB back without replaying the outbox (`outbound-reconciliation.md`).
- Evidence (rollback tag):

## Unresolved / non-blocking observations

- (None yet)

## Local gate evidence (recorded automatically, no Meta required)

- `dotnet build UnifiedInbox.slnx --no-restore --warnaserror`: 0 warnings / 0 errors.
- `dotnet test` with `RUN_DOCKER_TESTS=true FAIL_ON_SKIPPED=true`: all suites, 0 skipped.
- `dotnet ef migrations has-pending-model-changes`: clean.
- Frontend: `bun run test --run`, `bunx tsc --noEmit`, `bun run build`: green.
- `docker compose config --quiet`: ok; empty-volume `docker compose up -d --build` healthy.
- `scripts/ci-smoke.py`: register -> verify -> login and webhook -> inbox PASSED.
- Playwright: 15 passed / 3 env-gated skips (need Meta secrets) / 0 failed.
- `./scripts/verify-mvp.ps1` (pwsh): exit 0.
