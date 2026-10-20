param([string]$Repository = 'zzaiccf/xa-sk5', [string]$ReleaseDirectory = '', [switch]$Publish)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content -LiteralPath "$repoRoot/Directory.Build.props" -Raw)).Project.PropertyGroup.Version
$release = if ($ReleaseDirectory) { [IO.Path]::GetFullPath($ReleaseDirectory, $repoRoot) } else { Join-Path $repoRoot "artifacts/release/$version" }
$expected = @("xa-sk5-$version-win-x64.zip","xa-sk5-Setup-$version.exe")
foreach ($name in $expected) { if (!(Test-Path -LiteralPath (Join-Path $release $name))) { throw "Missing release asset: $name" } }
$manifest = Get-Content -LiteralPath (Join-Path $release 'latest.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne $version) { throw 'Manifest version mismatch.' }
$zip = Join-Path $release $manifest.portable.url
if ((Get-FileHash -LiteralPath $zip).Hash -ne $manifest.portable.sha256 -or (Get-Item -LiteralPath $zip).Length -ne $manifest.portable.size) { throw 'Portable ZIP integrity mismatch.' }
foreach ($line in Get-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([A-Fa-f0-9]{64})  ([^/\\]+)$') { throw 'Invalid checksum list.' }
    if ((Get-FileHash -LiteralPath (Join-Path $release $Matches[2])).Hash -ne $Matches[1]) { throw 'Release asset checksum mismatch.' }
}
$assets = @($expected | ForEach-Object { Join-Path $release $_ })
$tag = "v$version"
& gh release view $tag --repo $Repository --json isDraft 2>$null
if ($LASTEXITCODE) {
    & gh release create $tag --repo $Repository --title "xa sk5 $version" --notes-file "$repoRoot/release/notes-$version.txt" --draft @assets
    if ($LASTEXITCODE) { throw 'Draft release creation/upload failed.' }
} else {
    $existing = & gh release view $tag --repo $Repository --json isDraft | ConvertFrom-Json
    if (!$existing.isDraft) { throw 'Published release assets are immutable; bump the source version.' }
    & gh release upload $tag --repo $Repository --clobber @assets
    if ($LASTEXITCODE) { throw 'Draft release asset upload failed.' }
}
if ($Publish) { & gh release edit $tag --repo $Repository --draft=false --latest; if ($LASTEXITCODE) { throw 'Publishing failed.' } }
