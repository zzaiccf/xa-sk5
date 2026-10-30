param(
    [ValidateSet('Plan','Backup','Snapshot','Observe','Install','Rollback')][string]$Step='Plan',
    [switch]$RunApproved,[switch]$GamesClosed,
    [string]$OutputDirectory,[string]$SandboxIni,[string[]]$BoxNames,
    [string]$SetupPath,[string]$ExpectedSetupSha256,
    [string]$ToolsPath="$env:ProgramFiles\WindowSocks\WindowSocks.Tools.exe",
    [int]$Minutes=30
)
$ErrorActionPreference='Stop'
if(!$RunApproved -or $Step -eq 'Plan'){
    Write-Output 'PLAN ONLY. C steps: backup -> approved 0.1.31 Setup -> sandbox exit/system curl -> scoped real handle handoff -> approved service crash/WFP -> external FIN/RST capture -> 30-minute dual-account observation. See docs/LIVE-ACCEPTANCE-0.1.31.md. No action taken.';return
}
if(!$OutputDirectory){throw 'Supply a unique local -OutputDirectory for this acceptance session.'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if($OutputDirectory.StartsWith([IO.Path]::GetFullPath("$env:ProgramData\WindowSocks")+'\',[StringComparison]::OrdinalIgnoreCase) -or $OutputDirectory -ieq [IO.Path]::GetFullPath("$env:ProgramData\WindowSocks")){throw 'Acceptance output must be outside the live ProgramData directory.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
switch($Step){
    'Backup' {
        if(!$GamesClosed){throw 'Confirm A/B games closed with -GamesClosed.'}
        if(!$SandboxIni -or !(Test-Path -LiteralPath $SandboxIni) -or !$BoxNames){throw 'Supply the actual Sandboxie.ini and exact Xa_ box names.'}
        if(@($BoxNames | Where-Object {$_ -notmatch '^Xa_[0-9a-f]{24}$'}).Count){throw 'Only xa sk5 dedicated box sections are allowed.'}
        $destination=Join-Path $OutputDirectory 'backup'
        if(Test-Path -LiteralPath $destination){throw 'Backup already exists; choose another directory. Never overwrite the recovery copy.'}
        New-Item -ItemType Directory -Path $destination | Out-Null
        Copy-Item -LiteralPath "$env:ProgramData\WindowSocks" -Destination (Join-Path $destination 'WindowSocks') -Recurse
        $text=[IO.File]::ReadAllText($SandboxIni)
        foreach($box in $BoxNames){
            $pattern='(?ms)^\['+[regex]::Escape($box)+'\]\s*\r?\n.*?(?=^\[|\z)'
            $match=[regex]::Match($text,$pattern)
            if(!$match.Success){throw "Section $box not found; backup incomplete."}
            [IO.File]::WriteAllText((Join-Path $destination ($box+'.ini-section')),$match.Value,[Text.UTF8Encoding]::new($false))
        }
        Get-ChildItem -LiteralPath $destination -Recurse -File | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'backup-hashes.json') -Encoding UTF8
        Write-Output 'Backup complete. Contains private encrypted settings/logs; keep local. No Sandboxie settings changed.'
    }
    {$_ -in 'Install','Rollback'} {
        if(!$GamesClosed){throw 'Confirm A/B games closed with -GamesClosed.'}
        if(!(Test-Path -LiteralPath (Join-Path $OutputDirectory 'backup-hashes.json'))){throw 'Complete and verify this session backup first.'}
        if(!$SetupPath -or !$ExpectedSetupSha256){throw 'Supply a reviewed Setup package and its previously recorded SHA-256.'}
        if((Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash -ine $ExpectedSetupSha256){throw 'Setup hash mismatch.'}
        $version=(Get-Item -LiteralPath $SetupPath).VersionInfo.ProductVersion
        if($Step -eq 'Install' -and $version -notmatch '^0\.1\.31(?:\D|$)'){throw 'Install requires 0.1.31 Setup.'}
        if($Step -eq 'Rollback' -and $version -notmatch '^0\.1\.(29|30)(?:\D|$)'){throw 'Rollback requires reviewed 0.1.29 or 0.1.30 Setup.'}
        # The user reviews the normal Setup wizard; no silent install or force stop.
        $process=Start-Process -FilePath ([IO.Path]::GetFullPath($SetupPath)) -PassThru -Wait
        if($process.ExitCode){throw "Setup exited $($process.ExitCode). Preserve backups and inspect its log."}
        Write-Output 'Setup returned success. Verify installed/service DLL hashes and version manually; rollback may require the documented dedicated WFP cleanup before starting games.'
    }
    {$_ -in 'Snapshot','Observe'} {
        if(!(Test-Path -LiteralPath $ToolsPath)){throw 'Supply installed Tools path.'}
        if($Minutes -lt 30 -or $Minutes -gt 120){throw 'Observation duration must be 30-120 minutes.'}
        $deadline=if($Step -eq 'Observe'){(Get-Date).AddMinutes($Minutes)}else{Get-Date}
        do {
            $stamp=Get-Date -Format yyyyMMdd-HHmmss
            $raw=& $ToolsPath pipe snapshot
            if($LASTEXITCODE){throw 'Snapshot failed; stop acceptance and preserve logs.'}
            # Omit proxy credentials, launch tickets and unbounded raw logs.
            $snapshot=($raw -join "`n") | ConvertFrom-Json
            $safe=[ordered]@{at=(Get-Date).ToString('o');diagnostics=($snapshot.diagnostics | Select-Object serviceOnline,engineRunning,version,detail);sandboxInstances=@($snapshot.sandboxInstances | Select-Object id,name,boxName,proxyId,enabled);sandboxStatuses=$snapshot.sandboxStatuses;statuses=$snapshot.statuses;globalProxyRouting=$snapshot.globalProxyRouting}
            $safe | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $OutputDirectory ($stamp+'-snapshot.json')) -Encoding UTF8
            if($Step -eq 'Observe'){Start-Sleep -Seconds 5}
        }while((Get-Date) -lt $deadline)
        Write-Output 'Snapshots saved. Observation alone is not a success verdict; review disconnects, proxy mapping and external packet evidence.'
    }
}
