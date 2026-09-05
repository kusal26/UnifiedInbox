# AGENTS.md

Guidance for agentic tools working in this repository. Read this before changing code.

## What this is

Tenant-isolated WhatsApp-first shared inbox ("Unified Inbox"). Stack: .NET 10 /
ASP.NET Core, EF Core + PostgreSQL (forced RLS), RabbitMQ, Redis/SignalR, MinIO, ClamAV,
React 19 + TanStack Query + Vite (bun), Vitest, Playwright, Testcontainers.

Architecture rule: `Tenants` and `ProviderRoutes` are the only unscoped routing data. Every
operation resolves a tenant, then runs inside a transaction-scoped `app.current_tenant`
(`ITenantExecutionScope`). Runtime code runs as the `NOBYPASSRLS` `app_runtime` role; only
the schema-owner migrator connection may bypass filters. Do not add `IgnoreQueryFilters()`
under `UnifiedInbox.Api`, `UnifiedInbox.Worker`, or production service files (an
architecture test fails if you do).

## Repository layout

- `src/backend/UnifiedInbox.{Domain,Application,Infrastructure,Api,Worker}` — layered .NET.
- `src/frontend/` — React SPA. `tests/e2e/` — Playwright (hits the built Compose stack).
- `tests/UnifiedInbox.{Domain,Application,Api,Integration,Architecture}Tests` — xUnit.
- `db/init/` — postgres entrypoint scripts (creates `app_runtime` role).
- `docs/superpowers/plans/` — implementation plans, executed task-by-task.
- `docs/releases/` — release/acceptance evidence (`whatsapp-production-acceptance.md`).
- `CHANNEL_FRONTEND_BACKEND_INTEGRATION.md` — FUTURE-channel (Messenger/Instagram/TikTok)
  reference ONLY. The shipped integration is WhatsApp. Do not treat it as normative for today.
- `docs/COMPLETION_PLAN.md` — status is evidence-linked, not aspirational.

## Environment (important — this bites everyone once)

- The repo lives on the **WSL filesystem** (`\\wsl.localhost\Ubuntu-24.04\home\kusal\UnifiedInbox`)
  and `git`/`dotnet`/`bun`/`docker`/`pwsh` are **WSL-native**.
- The interactive shell is **Windows PowerShell**. To run native tools:
  `wsl -- bash -lc '<command>'`. PowerShell mangles `$`, `%`, pipes, and nested quotes
  inside that string. **Rule of thumb:** write non-trivial bash/python/mjs snippets to a file
  under `C:\Users\LENOVO\AppData\Local\Temp\opencode\` and run
  `wsl -- bash -lc 'bash /mnt/c/Users/LENOVO/AppData/Local/Temp/opencode/x.sh'`.
- WSL tool paths: `~/.dotnet/tools/pwsh`, `~/.bun/bin/bun`. Export them in scripts:
  `export PATH=/home/kusal/.dotnet/tools:/home/kusal/.bun/bin:/usr/local/bin:/usr/bin:/bin`.
- Use the Read/Write/Edit/Glob/Grep tools on `\\wsl.localhost\...` paths normally; only
  *commands* must go through `wsl --`.

## Commands (run in WSL)

```bash
cd ~/UnifiedInbox
export PATH=/home/kusal/.dotnet/tools:/home/kusal/.bun/bin:/usr/local/bin:/usr/bin:/bin
export RUN_DOCKER_TESTS=true FAIL_ON_SKIPPED=true   # REQUIRED: docker suites then run, not skip

dotnet restore UnifiedInbox.slnx
dotnet build UnifiedInbox.slnx --no-restore --warnaserror   # must stay 0-warning
dotnet test UnifiedInbox.slnx --no-build                     # full suite incl. Testcontainers
dotnet ef migrations has-pending-model-changes --project src/backend/UnifiedInbox.Infrastructure --startup-project src/backend/UnifiedInbox.Api --no-build

# Frontend
cd src/frontend && bun install --frozen-lockfile && bun run test --run && bunx tsc --noEmit && bun run build

# Stack + smoke + e2e
docker compose down -v && docker compose up -d --build
python3 scripts/ci-smoke.py                                  # register->verify->login + webhook->inbox
cd tests/e2e && BASE_URL=http://localhost:8080 API_URL=http://localhost:8080 bunx playwright test

# Full scripted gate (docker required, slow)
pwsh -NoProfile -File ./scripts/verify-mvp.ps1
```

## Database / migrations

- ONLY the one-shot `migrator` Compose service (or `--migrate`) applies migrations. The API
  and worker never call `MigrateAsync()` on startup. Preserve this.
- Add migrations from the Infrastructure project with the Api startup project
  (`dotnet ef migrations add <Name> --project src/backend/UnifiedInbox.Infrastructure --startup-project src/backend/UnifiedInbox.Api`).
- All tenant-scoped rows carry `TenantId`; tenant-aware composite FKs are enforced
  (`(TenantId, Id)` principal keys) — keep them when adding entities.

## Wire / serialization contracts (easy to get wrong)

- Enums serialize **numerically** (default `System.Text.Json`): `UserRole` is
  `0=Owner,1=Admin,2=Agent`. Frontend `normalizeRole` maps numbers→names on read and MUST
  send `roleIndex(name)` on writes (`admin.invite`/`setRole`). Never send `"Agent"` etc.
- Refresh token cookie: `Path=/api/v1/auth`, `HttpOnly`, `Secure`, `SameSite=Strict`.
- Session list excludes revoked tokens (`RevokedAt == null`).
- Errors are RFC 7807 with stable `code` (`invalid_request`, `invalid_credentials`,
  `messaging_window_closed`, `channel_authorization_expired`, `asset_already_connected`,
  `provider_rate_limited`, `malicious_attachment`, `token_reuse_detected`, ...).

## Frontend conventions

- Server state via TanStack Query; optimistic mutations with rollback; targeted SignalR
  cache updates.
- `AuthProvider` exposes `ready` (false while `/auth/refresh` session restore is in flight);
  `ProtectedApp` must gate on `ready` before redirecting to `/login` (prevents reloads from
  dumping signed-in users to the login screen).
- Full page reloads/`page.goto` CAN abort in-flight fetches — in tests, await the resulting
  navigation (e.g. `waitForURL`) after a submit that triggers one.

## Docker / Compose gotchas (all hit in production of this repo)

- Postgres 18 image refuses a data volume mounted at `/var/lib/postgresql/data`; mount the
  parent `/var/lib/postgresql`.
- Compose v5 (this machine) rejects unquoted `${VAR:-default}` inside flow mappings — quote
  them: `"${VAR:-default}"`.
- The worker Dockerfile final stage MUST be `mcr.microsoft.com/dotnet/aspnet:10.0` (not
  `dotnet/runtime`); the worker needs the ASP.NET Core shared framework.
- After a compose operation that recreates the API container, nginx can cache the old API IP
  and return `502 Host is unreachable`. Fix: `docker compose restart frontend` (or recreate).
- Rate limiter: 120 req/min/partition in Production, 1200 elsewhere (Development/Test). All
  anonymous E2E traffic arrives behind one nginx IP, so keep test runs from hammering it.
- E2E specs `inbound-message.spec.ts` and `messaging.spec.ts` are environment-gated and skip
  unless `WHATSAPP_APP_SECRET` + `WHATSAPP_PHONE_NUMBER_ID` are set (real connected number) —
  that belongs to staging acceptance, not the local/CI gate.

## Testing expectations

- CI sets `RUN_DOCKER_TESTS=true` and `FAIL_ON_SKIPPED=true`; the `DockerFact` attributes
  then THROW instead of skipping if Docker isn't available. Never "fix" a failing docker
  test by skipping it.
- Backend suites must pass with **0 skipped** when docker is up.
- Playwright runs against the built Compose stack on `localhost:8080`
  (`BASE_URL`/`API_URL`); its webServer starts vite with `bun --cwd ../../src/frontend dev`
  (bun 1.4 rejects the old `... run dev --host ...` form).
- `scripts/ci-smoke.py` is the compose smoke (register→verify→login, webhook handshake,
  unsigned-webhook 401, authenticated inbox read). Keep it in sync with auth/route changes.

## Working style

- Task plans live in `docs/superpowers/plans/` and are executed task-by-task with checkboxes;
  each task commits when its targeted tests pass. Do not jump ahead of the plan's gates.
- Commit messages are conventional commits; this repo is driven from WSL so set repo-local
  `git config user.name` / `user.email` if missing before committing.
- `docs/superpowers/plans/2026-09-05-task10-progress-state.md` records the current Task 10
  savepoint (blocked only on real-Meta staging acceptance).
