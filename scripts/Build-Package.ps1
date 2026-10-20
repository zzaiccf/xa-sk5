param([string]$Version = '', [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent), [string]$OutputDirectory = '', [string]$PayloadDirectory = 'artifacts/release-payload')
# One release pipeline; the version is always derived from Directory.Build.props.
$ErrorActionPreference = 'Stop'
$actual = ([xml](Get-Content -LiteralPath (Join-Path $RepoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($Version -and $Version -ne $actual) { throw "Requested version $Version differs from source $actual." }
& "$PSScriptRoot/Build.ps1" -PayloadDirectory $PayloadDirectory
if ($LASTEXITCODE) { throw 'Release build failed.' }
& "$PSScriptRoot/Package.ps1" -PayloadDirectory $PayloadDirectory -OutputDirectory $OutputDirectory
