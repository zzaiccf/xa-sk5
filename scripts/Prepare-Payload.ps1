param([string]$PayloadDirectory = 'artifacts/local')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$payload = [System.IO.Path]::GetFullPath($PayloadDirectory, $repoRoot)
foreach ($file in @('WindowSocks.exe','WindowSocks.Service.exe','WindowSocks.Tools.exe','WindowSocks.Maintenance.exe','WindowSocks.Updater.exe','WindowSocksNative.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $file))) { throw "Missing $file; run Build.ps1 first." }
}
# Keep the original apphost for existing scripts. Avoid touching an identical running apphost.
$original = Join-Path $payload 'WindowSocks.exe'
$branded = Join-Path $payload 'xa sk5.exe'
if (!(Test-Path -LiteralPath $branded) -or (Get-FileHash -LiteralPath $original).Hash -ne (Get-FileHash -LiteralPath $branded).Hash) {
    Copy-Item -LiteralPath $original -Destination $branded -Force
}
foreach ($file in @('docs/操作說明.html','THIRD-PARTY-NOTICES.txt','LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $payload -Force
}
New-Item -ItemType Directory -Path "$payload/licenses" -Force | Out-Null
Copy-Item -Path "$repoRoot/licenses/*" -Destination "$payload/licenses" -Force
Copy-Item -LiteralPath "$repoRoot/third_party/ProxyBridge/LICENSE" -Destination "$payload/licenses/ProxyBridge-MIT.txt" -Force
Copy-Item -LiteralPath "$repoRoot/native/include/WinDivert-LICENSE.txt" -Destination "$payload/licenses/WinDivert-LICENSE.txt" -Force
# Local builds do not download drivers or change security settings.
$driverRoot = Join-Path $repoRoot 'third_party/WinDivert-2.2.2-A/x64'
if ((Test-Path "$driverRoot/WinDivert.dll") -and (Test-Path "$driverRoot/WinDivert64.sys")) {
    if ((Get-AuthenticodeSignature "$driverRoot/WinDivert64.sys").Status -ne 'Valid') { throw 'Driver signature is not valid; refusing to copy it.' }
    Copy-Item "$driverRoot/WinDivert.dll","$driverRoot/WinDivert64.sys" -Destination $payload -Force
}
$payloadDll = Test-Path -LiteralPath "$payload/WinDivert.dll"
$payloadSys = Test-Path -LiteralPath "$payload/WinDivert64.sys"
if ($payloadDll -ne $payloadSys) { throw 'Incomplete WinDivert pair in payload; use a clean output directory.' }
if ($payloadSys -and (Get-AuthenticodeSignature "$payload/WinDivert64.sys").Status -ne 'Valid') { throw 'Driver signature in actual payload is not valid.' }
# Both local repair and release installers consume the same integrity manifest.
Copy-Item -LiteralPath (Join-Path $repoRoot 'release/update-source.json') -Destination $payload -Force
# Refuse to publish personal state or unexpected content from a reused local folder.
foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\','/')
    $allowed = $relative -match '^licenses/[^/]+\.txt$' -or $relative -match '^(cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant)/[^/]+\.resources\.dll$' -or $relative -in @('LICENSE','THIRD-PARTY-NOTICES.txt','操作說明.html','install-manifest.json','update-source.json') -or $relative -match '^[^/]+\.(dll|exe|pdb|sys)$' -or $relative -match '^[^/]+\.(deps|runtimeconfig)\.json$'
    if (!$allowed -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Payload contains unexpected or personal content: $relative. Use a clean output directory." }
}
$manifest = @(Get-ChildItem $payload -Recurse -File | Where-Object { $_.Name -ne 'install-manifest.json' } | ForEach-Object {
    @{ path = [System.IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
ConvertTo-Json -InputObject $manifest -Depth 3 | Set-Content -LiteralPath "$payload/install-manifest.json" -Encoding utf8NoBOM
