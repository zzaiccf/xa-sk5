param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$out=[IO.Path]::GetFullPath($OutputDirectory,$repoRoot)
if(Test-Path -LiteralPath $out){throw 'Choose a new test output directory.'}
New-Item -ItemType Directory -Path $out | Out-Null
$vsWhere="${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath=& $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$toolset=Get-ChildItem "$vsPath/VC/Tools/MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot="${env:ProgramFiles(x86)}/Windows Kits/10"
$sdk=Get-ChildItem "$sdkRoot/Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$env:INCLUDE="$($toolset.FullName)/include;$($sdk.FullName)/ucrt;$($sdk.FullName)/shared;$($sdk.FullName)/um"
$env:LIB="$($toolset.FullName)/lib/x64;$sdkRoot/Lib/$($sdk.Name)/ucrt/x64;$sdkRoot/Lib/$($sdk.Name)/um/x64"
$compiler=Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe'
$hostFile=Join-Path $out 'fixture-host.exe'
$launcher=Join-Path $out 'fixture-launcher.exe'
& $compiler /nologo /std:c++20 /EHsc /O2 /MT /utf-8 (('/Fe:'+$hostFile)) (('/Fo:'+(Join-Path $out 'fixture-host.obj'))) (Join-Path $repoRoot 'tests/fixtures/SetupBootstrapHost.cpp') /link /SUBSYSTEM:WINDOWS user32.lib shell32.lib
if($LASTEXITCODE){throw 'Test host compilation failed.'}
& "$PSScriptRoot/Build-SetupBootstrap.ps1" -HostExecutable $hostFile -OutputPath $launcher
if(-not $?){throw 'Test launcher compilation failed.'}
function Start-Fixture([string]$mode,[bool]$verify=$false) {
 $info=[Diagnostics.ProcessStartInfo]::new($launcher)
 $info.UseShellExecute=$false
 $info.Environment['XA_SK5_BOOTSTRAP_FIXTURE']=$mode
 if($verify){$info.ArgumentList.Add('--verify-payload')}
 return [Diagnostics.Process]::Start($info)
}
function Find-Log([int]$testProcessId) {
 $root=Join-Path $env:LOCALAPPDATA 'WindowSocks/Setup'
 foreach($directory in @(Get-ChildItem -LiteralPath $root -Directory -Filter 'bootstrap-*' | Sort-Object LastWriteTime -Descending | Select-Object -First 12)) {
  $path=Join-Path $directory.FullName 'bootstrap.log'
  if(Test-Path -LiteralPath $path) {
   $text=[IO.File]::ReadAllText($path)
   if($text -match ('NativePid='+$testProcessId+'\r?\n')){return [pscustomobject]@{Path=$path;Text=$text}}
  }
 }
 return $null
}
function Wait-FixtureLog([int]$testProcessId,[string]$marker) {
 $deadline=[DateTime]::UtcNow.AddSeconds(12)
 do {
  $entry=Find-Log $testProcessId
  if($entry -and $entry.Text.Contains($marker)){return $entry}
  Start-Sleep -Milliseconds 100
 } while([DateTime]::UtcNow -lt $deadline)
 throw 'Expected preparation log was not recorded.'
}
$normal=Start-Fixture 'normal'
try {
 if(-not $normal.WaitForExit(15000) -or $normal.ExitCode -ne 0){throw 'Normal preparation/handoff failed.'}
 $entry=Wait-FixtureLog $normal.Id 'PreparationWorkerFinished'
 $visible=[int64]([regex]::Match($entry.Text,'PreparationVisibleMs=(\d+)').Groups[1].Value)
 $ready=[int64]([regex]::Match($entry.Text,'InstallerReadyMs=(\d+)').Groups[1].Value)
 if($visible -ge $ready -or $ready -lt 1000){throw 'Feedback did not precede the delayed host.'}
 if(Test-Path -LiteralPath (Join-Path (Split-Path $entry.Path -Parent) 'SetupHost.exe')){throw 'The completed staged host was not cleaned up.'}
 Write-Output "PASS: Native feedback at ${visible}ms; delayed host ready at ${ready}ms; handoff and cleanup succeeded."
} finally {$normal.Dispose()}
$failed=Start-Fixture 'fail'
try {
 $entry=Wait-FixtureLog $failed.Id 'PreparationWorkerFinished'
 if($failed.HasExited -or -not $entry.Text.Contains('42')){throw 'A failed host did not retain visible error feedback.'}
 Write-Output 'PASS: Early host failure retains the preparation/error window and diagnostic log.'
} finally {
 # This exact test process is already in its terminal error state. No real installer or service is stopped.
 if(-not $failed.HasExited){$failed.Kill();$failed.WaitForExit()}
 $failed.Dispose()
}
$verify=Start-Fixture 'normal' $true
try {
 if(-not $verify.WaitForExit(10000) -or $verify.ExitCode -ne 0){throw 'Headless verification failed.'}
 $entry=Wait-FixtureLog $verify.Id 'PreparationWorkerFinished'
 if(-not $entry.Text.Contains('without a preparation window')){throw 'Verification did not preserve its headless mode.'}
 Write-Output 'PASS: Headless verify-payload remains supported. No registry or service changes.'
} finally {$verify.Dispose()}
