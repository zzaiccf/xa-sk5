param([Parameter(Mandatory)][string]$Destination, [string]$StagingDirectory = '')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination }
$files = @(Get-Item -LiteralPath "$repoRoot/.gitignore","$repoRoot/Directory.Build.props","$repoRoot/Directory.Build.targets","$repoRoot/WindowSocks.sln","$repoRoot/README.md","$repoRoot/LICENSE","$repoRoot/THIRD-PARTY-NOTICES.txt")
$files += Get-ChildItem "$repoRoot/src","$repoRoot/native","$repoRoot/tests","$repoRoot/licenses","$repoRoot/third_party/ProxyBridge","$repoRoot/release" -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.git)[\\/]' }
foreach ($name in @('Build.ps1','Build-Native.ps1','Build-SetupBootstrap.ps1','Build-Package.ps1','Prepare-Payload.ps1','Package.ps1','Export-ReleaseSource.ps1','Publish-GitHubRelease.ps1','Fetch-Dependencies.ps1','Generate-BrandAssets.py','Test-UpdateRelease.ps1','Test-SetupBootstrap.ps1')) { $path=Join-Path "$repoRoot/scripts" $name; if(Test-Path -LiteralPath $path){$files += Get-Item -LiteralPath $path} }
foreach ($name in @('操作說明.html','ARCHITECTURE.md','UPSTREAM-CHANGES.md','REMOTE-CONTROL-BYPASS.md','RELEASING.md')) { $path=Join-Path "$repoRoot/docs" $name; if(Test-Path -LiteralPath $path){$files += Get-Item -LiteralPath $path} }
$zip = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source contains a reparse point.' }
        $relative = [IO.Path]::GetRelativePath($repoRoot, $file.FullName).Replace('\','/')
        if ($relative -match '(?i)(settings\.dpapi|owner\.sid|\.pfx$|\.secrets\.|\.env$)') { throw "Refusing to export private state: $relative" }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        if ($StagingDirectory) { $target = Join-Path $StagingDirectory $relative; New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null; Copy-Item -LiteralPath $file.FullName -Destination $target }
    }
} finally { $zip.Dispose() }
