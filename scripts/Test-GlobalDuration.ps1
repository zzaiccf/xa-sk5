$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks/WindowSocks.Desktop.PerformanceChecks.csproj -c Release -- --global-duration
    if($LASTEXITCODE){throw 'Global duration regression failed.'}
} finally { Pop-Location }
