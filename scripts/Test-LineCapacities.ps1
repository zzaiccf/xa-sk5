$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks/WindowSocks.Desktop.PerformanceChecks.csproj -c Release -- --line-capacities
    if($LASTEXITCODE){throw 'Line capacity UI/backend regression failed.'}
} finally { Pop-Location }
