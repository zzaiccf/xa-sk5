$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks/WindowSocks.Desktop.PerformanceChecks.csproj -c Release -- --bulk-actions
    if ($LASTEXITCODE) { throw '批次清除／關閉進程介面驗證失敗。' }
} finally { Pop-Location }
