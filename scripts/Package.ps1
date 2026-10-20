param([switch]$FrameworkDependent, [string]$PayloadDirectory = 'artifacts/release-payload', [switch]$PortableOnly, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    $version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
    $payload = [IO.Path]::GetFullPath($PayloadDirectory, $repoRoot)
    $release = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory, $repoRoot) } else { Join-Path $repoRoot "artifacts/release/$version" }
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    & "$PSScriptRoot/Prepare-Payload.ps1" -PayloadDirectory $payload
    foreach ($name in @('WinDivert.dll','WinDivert64.sys')) { if (!(Test-Path -LiteralPath (Join-Path $payload $name))) { throw "Release requires the signed official driver pair: $name" } }
    & (Join-Path $payload 'WindowSocks.Tools.exe') verify-release $payload $version
    if ($LASTEXITCODE) { throw 'Release version or payload integrity verification failed.' }
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    $zipName = "xa-sk5-$version-win-x64.zip"
    $zipPath = Join-Path $release $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item -LiteralPath $zipPath -Destination 'artifacts/package.zip' -Force
    if (!$PortableOnly) {
        dotnet publish src/WindowSocks.Setup/WindowSocks.Setup.csproj -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/setup
        if ($LASTEXITCODE) { throw 'Installer publish failed.' }
        $setup = Join-Path $release "xa-sk5-Setup-$version.exe"
        Copy-Item -LiteralPath 'artifacts/setup/WindowSocks-Setup.exe' -Destination $setup -Force
        $verify = Start-Process -FilePath $setup -ArgumentList '--verify-payload' -Wait -PassThru -WindowStyle Hidden
        if ($verify.ExitCode) { throw 'Final installer embedded payload verification failed.' }
    }
    & "$PSScriptRoot/Export-ReleaseSource.ps1" -Destination (Join-Path $release "xa-sk5-$version-source.zip")
    $manifest = [ordered]@{
        schemaVersion = 1; version = $version; publishedAt = [DateTimeOffset]::UtcNow.ToString('O')
        notes = [IO.File]::ReadAllText((Join-Path $repoRoot "release/notes-$version.txt"))
        portable = [ordered]@{ url = $zipName; size = (Get-Item -LiteralPath $zipPath).Length; sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash }
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $release 'latest.json') -Encoding utf8NoBOM
    Get-ChildItem -LiteralPath $release -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' -and $_.Name -ne 'payload-verification.txt' } | Sort-Object Name | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $(Split-Path $_.Path -Leaf)" } | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding utf8NoBOM
    Write-Host "Release ready: $release"
} finally { Pop-Location }
