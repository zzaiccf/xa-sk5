$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet run --project tests/WindowSocks.Desktop.PerformanceChecks/WindowSocks.Desktop.PerformanceChecks.csproj -c Release
    if ($LASTEXITCODE) { throw '規則編輯器效能／互動回歸測試失敗。' }
} finally { Pop-Location }
