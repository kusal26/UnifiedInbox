# Unified Inbox MVP

Tenant-scoped shared inbox MVP with a .NET 10 API/worker solution and a React/Vite client.

## Run locally

1. Copy `.env.example` to `.env` and start dependencies with `docker compose up -d`.
2. Run the API with `dotnet run --project src/backend/UnifiedInbox.Api`.
3. Run the client with `bun --cwd src/frontend install` and `bun --cwd src/frontend dev`.

Demo login: workspace `acme`, `agent@acme.test`, any non-empty password.

## Verify

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-mvp.ps1
```

The current MVP includes tenant-scoped login, conversation search/filtering, unified message/note activity, unread-safe cursors, inbound deduplication, outbound idempotency, webhook persistence boundary, SignalR tenant groups, and a responsive inbox UI.
