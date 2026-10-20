param([string]$PayloadDirectory = 'artifacts/release-payload', [string]$OldPayloadDirectory = 'artifacts/local', [string]$OutputDirectory = 'artifacts/update-validation')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$payload = [IO.Path]::GetFullPath($PayloadDirectory, $repoRoot)
$old = [IO.Path]::GetFullPath($OldPayloadDirectory, $repoRoot)
$out = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
if (Test-Path -LiteralPath $out) { throw 'Choose a new validation output directory; prior evidence is preserved.' }
New-Item -ItemType Directory -Path $out | Out-Null
$version = ([xml](Get-Content -LiteralPath "$repoRoot/Directory.Build.props" -Raw)).Project.PropertyGroup.Version
$target = Join-Path $out 'portable'
New-Item -ItemType Directory -Path $target | Out-Null
Copy-Item -Path "$old/*" -Destination $target -Recurse
# The content is synthetic; real credentials and user profiles are never copied into this sandbox.
[IO.File]::WriteAllBytes((Join-Path $target 'settings.dpapi'), [byte[]](13,37,42,99))
$before = (Get-FileHash -LiteralPath (Join-Path $target 'settings.dpapi')).Hash
$job = [ordered]@{ payload=$payload; target=$target; version=$version; restartExe=(Join-Path $target 'xa sk5.exe'); parentPid=0; parentStartTime=0; includeService=$false; recover=$false }
$jobFile = Join-Path $out 'job.json'
$job | ConvertTo-Json | Set-Content -LiteralPath $jobFile -Encoding utf8NoBOM
$worker = Start-Process -FilePath (Join-Path $payload 'WindowSocks.Updater.exe') -ArgumentList @('--worker',('"'+$jobFile+'"')) -PassThru -Wait -WindowStyle Hidden
$result = Get-Content -LiteralPath (Join-Path $out 'result.json') -Raw | ConvertFrom-Json
if ($worker.ExitCode -or !$result.success) { throw "Actual updater failed: $($result.message)" }
if ((Get-FileHash -LiteralPath (Join-Path $target 'settings.dpapi')).Hash -ne $before) { throw 'Synthetic user state changed.' }
& (Join-Path $target 'WindowSocks.Tools.exe') verify-release $target $version --installed
if ($LASTEXITCODE) { throw 'Post-update integrity check failed.' }
$report = [ordered]@{success=$true;version=$version;settingsPreserved=$true;serviceTouched=$false;backup=$result.backups;target=$target}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'validation.json') -Encoding utf8NoBOM
$report | ConvertTo-Json -Depth 5
