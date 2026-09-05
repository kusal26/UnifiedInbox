# Unified Inbox

A tenant-isolated shared inbox built with .NET 10, PostgreSQL, RabbitMQ, Redis/SignalR, MinIO-compatible object storage, and React.

## Local stack

1. Copy `.env.example` to `.env` and replace both key placeholders. `CREDENTIAL_MASTER_KEY` must decode to exactly 32 random bytes.
2. Run `docker compose up --build`.
3. Open `http://localhost:8080`. In Development only, the seed owner is `acme` / `owner@acme.test` / `Development!123`.

The dedicated one-shot `migrator` service is the only process that applies EF migrations. The API and worker never migrate on startup; they require the migrator to have completed successfully (see `docker-compose.yml` `depends_on: migrator: service_completed_successfully`). Development data and the fake WhatsApp adapter are enabled only when explicitly configured for Development/Test. Set `WHATSAPP_USE_FAKE=false`, configure the Graph credentials through the channel flow, and set `WHATSAPP_APP_SECRET` before receiving production traffic.

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
