$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks/WindowSocks.Desktop.PerformanceChecks.csproj -c Release -- --tray-behavior
    if($LASTEXITCODE){throw 'Tray/taskbar lifecycle regression failed.'}
} finally { Pop-Location }
