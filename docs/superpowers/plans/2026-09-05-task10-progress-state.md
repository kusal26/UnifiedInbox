# Task 10 Progress State — WhatsApp Production Completion

> **Resume point:** WIP savepoint commit `chore(release): WIP Task 10 gates ...` on branch
> `chore/whatsapp-production-acceptance-gates`. Everything below was verified green in this
> session; Steps 1–4 of Task 10 are effectively DONE, Step 5 (staging acceptance) is blocked
> on real Meta credentials, and Step 6 (record evidence + commit) is partially drafted.

## Where to pick up

Read `docs/superpowers/plans/2026-09-04-whatsapp-production-completion.md` Task 10, then:

1. **Step 5 (blocked, human + Meta):** run the staging acceptance against a real Meta
   staging test number. See `docs/runbooks/production-checklist.md` and fill
   `docs/releases/whatsapp-production-acceptance.md` (draft already exists below).
2. **Step 6:** record evidence in the release doc, commit.
3. Optional cleanups that are NOT done yet:
   - `.github/workflows/ci.yml` has not been run end-to-end on GitHub (no push made).
     `production-guards`/`compose-empty-db` are new jobs; validate on the first real push.
   - Consider running `dotnet ef migrations has-pending-model-changes` etc. once more after
     any later edits (all green at this savepoint).
   - Delete this progress file before final merge.

## Environment facts (how this repo is driven)

- Repo lives in WSL: `\\wsl.localhost\Ubuntu-24.04\home\kusal\UnifiedInbox`.
- The `bash` tool here actually runs **Windows PowerShell**; native WSL commands must be
  wrapped: `wsl -- bash -lc '<cmd>'`. `$` and nested quotes get mangled by PowerShell, so
  write scripts to files under `C:\Users\LENOVO\AppData\Local\Temp\opencode\*.sh|.mjs|.py`
  and run them via `wsl -- bash -lc 'bash /mnt/c/Users/LENOVO/.../x.sh'`.
- Tooling installed inside WSL: `dotnet` 10.0.109, `bun` 1.4.1 (at `~/.bun/bin`), Docker
  Compose v5, `pwsh` 7.6.5 (dotnet global tool, runnable as
  `/home/kusal/.dotnet/tools/pwsh`), Playwright chromium headless at `~/.cache/ms-playwright`.
- IMPORTANT Docker gotcha: this Docker Desktop install restarts containers spontaneously and
  nginx caches the API container IP at boot, so after any `docker compose up`/`restart` that
  recreates the API container you may see `502 Host is unreachable` from the frontend until
  you `docker compose restart frontend` (or recreate the whole stack). If you see 502s, first
  check `docker compose logs frontend` for `connect() failed`.

## Verified green at this savepoint (all executed, not assumed)

- `dotnet build UnifiedInbox.slnx --no-restore --warnaserror` — 0 warnings/errors.
- `RUN_DOCKER_TESTS=true FAIL_ON_SKIPPED=true dotnet test UnifiedInbox.slnx --no-build` —
  all suites passed with **0 skipped**:
  Architecture 2, Domain 5, Application 40, Integration 134, Api 50.
- `dotnet ef migrations has-pending-model-changes` — clean.
- Frontend: `bun run test --run` 51 passed, `bunx tsc --noEmit` clean, `bun run build` ok.
- `docker compose config --quiet` — ok (after quoting fixes below).
- Empty-volume boot: `docker compose down -v` + `docker compose up -d --build`; migrator
  completed; `/api/v1/operations/ready` 200.
- `scripts/ci-smoke.py` (register -> verify -> login, webhook handshake, unsigned-webhook
  401, authenticated inbox read) — PASSED.
- Playwright full suite against the Compose stack (default parallel workers):
  **15 passed, 3 skipped, 0 failed**. The 3 skips are gated on real WhatsApp secrets +
  a connected test number (`inbound-message.spec.ts`, `messaging.spec.ts`) — these belong to
  Step 5 staging acceptance, NOT to the docker/CI "required suites".
- `./scripts/verify-mvp.ps1` run under pwsh (WSL) — exit 0.

## Task 10 files changed / created (why each)

Documentation + CI (Step 1–3):
- `.github/workflows/ci.yml` — added `FAIL_ON_SKIPPED: true`; new `production-guards` job
  (runs `ProductionConfigurationTests` + `docker compose config --quiet`); rewrote
  `compose-empty-db` to boot from an empty volume and run `python3 scripts/ci-smoke.py`;
  `e2e` now `needs` the new jobs and polls the stack readiness via host curl.
- `scripts/ci-smoke.py` (new) — portable Python smoke used by CI and the local gate.
- `scripts/verify-mvp.ps1` — now sets `RUN_DOCKER_TESTS=true` + `FAIL_ON_SKIPPED=true`,
  builds with `--warnaserror`, runs the `ProductionConfigurationTests` filter explicitly,
  and appends `docker compose config --quiet`.
- `README.md` — migrator is now the only process that applies migrations.
- `docs/COMPLETION_PLAN.md` — status is now evidence-linked; Phase F marked
  "implementation landed; acceptance pending".
- `docs/runbooks/production-checklist.md` — evidence goes in the release doc; renamed the
  guard to `ProductionGuard.Validate`; added `production-guards` CI reference.
- `CHANNEL_FRONTEND_BACKEND_INTEGRATION.md` — header states it is future-channel reference
  only (current release is WhatsApp only).

Tests that enforce Docker-mandatory suites (Step 1):
- `tests/UnifiedInbox.IntegrationTests/DockerFactAttribute.cs` and
  `tests/UnifiedInbox.Api.Tests/DockerFactAttribute.cs` — now THROW in CI
  (`GITHUB_ACTIONS`/`CI`/`FAIL_ON_SKIPPED=true`) instead of skipping when
  `RUN_DOCKER_TESTS != true`.

## Real bugs found and fixed while driving the local gate (IMPORTANT)

These are genuine defects the E2E gate surfaced (not Task-10-scoped in the plan, but
required for "all Playwright scenarios passing"):

1. **Worker container never booted** — `src/backend/UnifiedInbox.Worker/Dockerfile` used
   `mcr.microsoft.com/dotnet/runtime:10.0` as its final base, which lacks
   `Microsoft.AspNetCore.App`; the worker failed at startup with "No frameworks were found".
   Fixed to `mcr.microsoft.com/dotnet/aspnet:10.0`. (This would have failed CI `containers`
   + every outbound path.)
2. **`docker-compose.yml` invalid for PG18 + Docker Compose v5** —
   - Postgres 18 images refuse a data volume mounted at `/var/lib/postgresql/data`; the
     volume must mount the parent `/var/lib/postgresql`. Fixed.
   - Compose v5 chokes on unquoted `${VAR:-default}` inside flow mappings; quoted them.
3. **Session revocation didn't remove the session from the list** —
   `AuthenticationService.SessionsAsync` never filtered `RevokedAt == null`, so a revoked
   refresh token still appeared. Fixed (`&& x.RevokedAt == null`).
4. **Frontend sent role NAMES, API expects role NUMBERS** — `api/admin.ts` `invite()` and
   `setRole()` sent `role: 'Agent'`; the API serializes `UserRole` numerically
   (0=Owner,1=Admin,2=Agent), so invites/role changes 400'd against the real backend. Added
   a `roleIndex()` mapping in `admin.ts` and made E2E specs send numeric roles.
5. **Reload dumped authenticated users to /login** — on a full page reload
   (`AuthProvider`) the SPA immediately rendered `ProtectedApp` with `token=null`, which
   navigated to `/login` BEFORE the `/auth/refresh` restore completed; after refresh
   returned a valid token nothing redirected back. Added a `ready` flag to `AuthProvider`
   (false while the refresh is in flight) and gated `ProtectedApp` on it (`App.tsx`).
6. **Rate-limiter flakiness during E2E** — every anonymous request (login, refresh,
   register, verify) from Playwright arrives behind the single nginx IP, and the old
   hard-coded `120 req/min` budget was exhausted by a full parallel suite -> cascading 429s
   -> "Invalid email or password" style failures. `Program.cs` now keeps Production at
   120/min but allows 1200/min outside Production.
7. **E2E spec race + over-strict locator** (`tests/e2e/specs/invitations.spec.ts`) —
   after clicking "Join workspace" the test immediately called `loginAs(...)` whose
   `page.goto('/login')` aborted the in-flight accept POST. Now awaits `waitForURL('**/login')`
   after accept (the app navigates there on success). Also scoped the post-invite assertion
   to the pending-invitation row (`li` with the email) because the toast text also contains
   the email (strict-mode violation).
8. **Playwright webServer command broken on bun 1.4** — `bun --cwd <dir> run dev --host ...`
   exits with usage on bun 1.4; changed `tests/e2e/playwright.config.ts` to
   `bun --cwd ../../src/frontend dev`.

## Blocker + why this is taking so long (for your context)

- **Hard blocker (Task 10 Step 5):** real-Meta staging acceptance needs a WhatsApp Business
  test number + approved Embedded Signup config + App Secret + a live webhook subscription.
  That is a human/credentials task — nothing in this repo can fake it, and the plan forbids
  fake-provider mode in Production. Everything up to that gate is done and green.
- **Why the local gate took many hours:** (a) `git`/`docker`/`dotnet`/`bun` are WSL-native
  but the shell tool is Windows PowerShell, so every command needed `wsl -- bash -lc`
  wrapping and escaping (a lot of back-and-forth); (b) Playwright needed browser downloads
  and a bun install inside WSL; (c) the E2E gate then surfaced **7 genuine product/CI bugs**
  (above), each of which had to be reproduced, root-caused, fixed, and re-verified against
  the full Docker stack — including two subtle races (reload-to-login, accept-request
  abort) and the anonymous rate-limit cascade that made failures look like flakiness;
  (d) repeated runs share one per-IP rate-limit window, so back-to-back test runs polluted
  each other until the limit was made env-aware.

## Reproduce / continue commands (run in WSL)

```bash
cd ~/UnifiedInbox
export PATH=/home/kusal/.dotnet/tools:/home/kusal/.bun/bin:/usr/local/bin:/usr/bin:/bin
# Full scripted gate (docker required; ~6-8 min):
pwsh -NoProfile -File ./scripts/verify-mvp.ps1
# Stack + smoke + e2e:
docker compose down -v && docker compose up -d --build
python3 scripts/ci-smoke.py
cd tests/e2e && BASE_URL=http://localhost:8080 API_URL=http://localhost:8080 bunx playwright test
```

## Staging acceptance checklist (Task 10 Step 5–6) — to be recorded in docs/releases/whatsapp-production-acceptance.md

- Complete Embedded Signup with a real staging number.
- Duplicate webhook delivery -> one receipt/message.
- Inbound text + each supported media type; outbound free-form, template, attachments.
- Revoke and repair access; health/notification/audit events.
- Backup/restore rehearsal with RTO/RPO; fake-mode rejection proof; image digests + rollback.
- Draft release doc below (uncommitted until Step 6 fills evidence).
