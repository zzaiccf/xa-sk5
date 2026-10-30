$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks/WindowSocks.Desktop.PerformanceChecks.csproj -c Release -- --unified-program-rules
    if($LASTEXITCODE){throw 'Unified program rule UI regression failed.'}
} finally { Pop-Location }
