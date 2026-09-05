$ErrorActionPreference = 'Stop'
$env:RUN_DOCKER_TESTS = 'true'
$env:FAIL_ON_SKIPPED = 'true'
dotnet restore UnifiedInbox.slnx
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build UnifiedInbox.slnx --no-restore --warnaserror
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test UnifiedInbox.slnx --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test tests/UnifiedInbox.Api.Tests --no-build --filter FullyQualifiedName~ProductionConfigurationTests
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
docker compose config --quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
