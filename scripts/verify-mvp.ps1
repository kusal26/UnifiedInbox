$ErrorActionPreference = 'Stop'
dotnet restore UnifiedInbox.slnx
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build UnifiedInbox.slnx --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test UnifiedInbox.slnx --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet ef migrations has-pending-model-changes --project src/backend/UnifiedInbox.Infrastructure --startup-project src/backend/UnifiedInbox.Api --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location src/frontend
bun run test --run
if ($LASTEXITCODE -ne 0) { Pop-Location; exit $LASTEXITCODE }
bunx tsc --noEmit
if ($LASTEXITCODE -ne 0) { Pop-Location; exit $LASTEXITCODE }
bun run build
if ($LASTEXITCODE -ne 0) { Pop-Location; exit $LASTEXITCODE }
Pop-Location
