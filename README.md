# Unified Inbox

A tenant-isolated shared inbox built with .NET 10, PostgreSQL, RabbitMQ, Redis/SignalR, MinIO-compatible object storage, and React.

## Local stack

1. Copy `.env.example` to `.env` and replace both key placeholders. `CREDENTIAL_MASTER_KEY` must decode to exactly 32 random bytes.
2. Run `docker compose up --build`.
3. Open `http://localhost:8080`. In Development only, the seed owner is `acme` / `owner@acme.test` / `Development!123`.

The dedicated one-shot `migrator` service is the only process that applies EF migrations. The API and worker never migrate on startup; they require the migrator to have completed successfully (see `docker-compose.yml` `depends_on: migrator: service_completed_successfully`). Development data and the fake WhatsApp adapter are enabled only when explicitly configured for Development/Test. Set `WHATSAPP_USE_FAKE=false`, configure the Graph credentials through the channel flow, and set `WHATSAPP_APP_SECRET` before receiving production traffic.

### Running it (quick reference)

Everything is Docker-native and driven from **WSL** (`git`/`dotnet`/`bun`/`docker` are WSL tools;
run commands through `wsl -- bash -lc '...'` from a Windows shell).

```bash
cd ~/UnifiedInbox
docker compose up -d --build   # build + start everything (migrator runs once, then api/worker)
docker compose down            # stop (data kept)
docker compose down -v         # full reset (wipes data volumes)
docker compose ps              # container status
docker compose logs -f api worker   # follow backend logs
```

What runs where:

| Service | Role | Reachable at |
| --- | --- | --- |
| `frontend` (nginx) | serves the SPA and proxies `/api` + `/hubs` to the API | http://localhost:8080 |
| `api` | ASP.NET Core HTTP API (register/login/webhooks/inbox/admin) | behind nginx, same origin `/api/v1` |
| `worker` | background jobs (outbox, webhook processing, retries, cleanup, health) | — |
| `migrator` | one-shot EF migrations (only migrator applies them) | runs to completion at startup |
| `postgres` | database (`app_runtime` role, forced RLS) | — |
| `rabbitmq` | broker/retry queues | — |
| `redis` | SignalR backplane | — |
| `minio` | attachment object storage | http://localhost:9000 |
| `mailpit` | captures all outbound email (verify/invite/reset tokens) | http://localhost:8025 |
| `clamav` | attachment virus scanning | — |

Health probes: http://localhost:8080/api/v1/operations/health and .../ready.

To use the app (register → verify → login), open http://localhost:8080 and read the verification
token out of Mailpit at http://localhost:8025. Real WhatsApp connect/send needs Meta credentials;
everything else (admin, team/invites, canned responses, notifications, audit, settings, metrics)
works fully locally.

## Security model

- JWT access tokens expire after 15 minutes. Rotating 30-day refresh tokens are hashed in PostgreSQL and sent only in Secure, HttpOnly, SameSite=Strict cookies.
- Tenant identity comes only from the authenticated claim. Named EF query filters, write guards, composite tenant indexes, and PostgreSQL row-level security provide layered isolation.
- Provider secrets use versioned AES-256-GCM envelopes. The deployment master key is never stored in the database.
- WhatsApp webhooks are validated over the raw request bytes before durable receipt/outbox persistence.
- Attachments are limited to 10 MB and the documented image, PDF, and MP4 content types. Object keys are tenant-scoped and uploaded filenames are sanitized.

## Verification

```powershell
dotnet test UnifiedInbox.slnx
Push-Location src/frontend
bun run test --run
bunx tsc --noEmit
bun run build
Pop-Location
docker compose config
```

See [backup and recovery](docs/runbooks/backup-recovery.md), [outbound reconciliation](docs/runbooks/outbound-reconciliation.md), and [webhook replay](docs/runbooks/webhook-replay.md) for operational procedures.
