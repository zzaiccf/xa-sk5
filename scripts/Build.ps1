param([switch]$FrameworkDependent, [string]$PayloadDirectory = 'artifacts/local', [switch]$Package)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    & "$PSScriptRoot/Build-Native.ps1"
    if ($LASTEXITCODE) { throw 'Native build failed.' }
    dotnet test tests/WindowSocks.Tests/WindowSocks.Tests.csproj -c Release --logger 'trx;LogFileName=core-tests.trx' --results-directory artifacts/test-results
    if ($LASTEXITCODE) { throw 'Tests failed.' }
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    $payload = [System.IO.Path]::GetFullPath($PayloadDirectory, $repoRoot)
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    foreach ($project in @('Desktop','Service','Tools','Maintenance','Updater')) {
        dotnet publish "src/WindowSocks.$project/WindowSocks.$project.csproj" -c Release -r win-x64 --self-contained $selfContained -o $payload
        if ($LASTEXITCODE) { throw "$project publish failed." }
    }
    if ($Package) {
        & "$PSScriptRoot/Package.ps1" -FrameworkDependent:$FrameworkDependent -PayloadDirectory $payload
    } else {
        & "$PSScriptRoot/Prepare-Payload.ps1" -PayloadDirectory $payload
        Write-Host "Local build ready: $payload"
        Write-Host "Run: $(Join-Path $payload 'xa sk5.exe')"
    }
} finally { Pop-Location }

