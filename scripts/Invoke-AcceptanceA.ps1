param([switch]$RunApproved,[switch]$ScheduleSystem,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(!$RunApproved){Write-Output 'PLAN ONLY: SYSTEM GET_VERSION/ABI; PID 4 and common protected-process birth queries; exact product WFP provider/sublayer reads. See docs/LIVE-ACCEPTANCE-0.1.31.md. No action taken.';return}
$root=Split-Path $PSScriptRoot -Parent
$exe=Join-Path $root 'artifacts/acceptance-tools/ReadOnlyAcceptance.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'First build with Build-AcceptanceTools.ps1 -Target ReadOnly.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts/acceptance/A-'+(Get-Date -Format yyyyMMdd-HHmmss))}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'system-read.txt'
if(Test-Path -LiteralPath $output){throw 'Read output already exists; select a fresh acceptance directory.'}
if(!$ScheduleSystem){
    & $exe --read-approved *> $output
    if($LASTEXITCODE){throw "Read probe reported $LASTEXITCODE. Review $output; no settings changed."}
    Get-Content -LiteralPath $output;return
}
# This optional transport creates/removes one scheduled task. It does not start
# or stop any service. Explicit consent for this transport is required in stage A.
$taskName='xa-sk5-readonly-'+[Guid]::NewGuid().ToString('N')
$escapedExe=$exe.Replace("'","''");$escapedOutput=$output.Replace("'","''")
$command="& '$escapedExe' --read-approved *> '$escapedOutput'; exit `$LASTEXITCODE"
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$action=New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument "-NoProfile -NonInteractive -EncodedCommand $encoded"
try {
    Register-ScheduledTask -TaskName $taskName -Action $action -User SYSTEM -RunLevel Highest | Out-Null
    Start-ScheduledTask -TaskName $taskName
    $deadline=(Get-Date).AddSeconds(45)
    do {Start-Sleep -Milliseconds 250;$info=Get-ScheduledTaskInfo -TaskName $taskName;$state=(Get-ScheduledTask -TaskName $taskName).State}while((!(Test-Path -LiteralPath $output) -or $state -eq 'Running') -and (Get-Date) -lt $deadline)
    if($state -eq 'Running'){Stop-ScheduledTask -TaskName $taskName;throw 'Read-only probe timed out.'}
    if(!(Test-Path -LiteralPath $output)){throw 'Read probe produced no output.'}
    Get-Content -LiteralPath $output
    if($info.LastTaskResult -ne 0){throw "Read probe returned $($info.LastTaskResult); inspect result."}
} finally {if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){Unregister-ScheduledTask -TaskName $taskName -Confirm:$false}}
