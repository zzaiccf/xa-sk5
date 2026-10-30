param([switch]$RunApproved,[string]$Endpoint,[int]$Port=0)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root 'artifacts/wfp-integration'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$vsWhere="${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs=& $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$vs){throw 'Visual Studio C++ tools required.'}
$toolset=Get-ChildItem "$vs/VC/Tools/MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot="${env:ProgramFiles(x86)}/Windows Kits/10"
$sdk=Get-ChildItem "$sdkRoot/Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$env:INCLUDE="$($toolset.FullName)/include;$($sdk.FullName)/ucrt;$($sdk.FullName)/shared;$($sdk.FullName)/um"
$env:LIB="$($toolset.FullName)/lib/x64;$sdkRoot/Lib/$($sdk.Name)/ucrt/x64;$sdkRoot/Lib/$($sdk.Name)/um/x64"
$compiler=Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe'
Push-Location $out
try {
    & $compiler /nologo /std:c++20 /EHsc /O2 /MT /utf-8 /W4 "$root/tests/WindowSocks.Native.PerformanceChecks/WfpIntegration.cpp" /Fe:WfpIntegration.exe /link ws2_32.lib advapi32.lib
    if($LASTEXITCODE){throw 'WFP fixture build failed.'}
    if(!$RunApproved){Write-Output 'Built only. No WFP filters, network probes, or services were touched.';return}
    if(!$Endpoint -or $Port -lt 1 -or $Port -gt 65535){throw 'Supply an approved reachable test endpoint and TCP port.'}
    & ./WfpIntegration.exe --run-approved $Endpoint $Port
    if($LASTEXITCODE){throw 'WFP integration failed; inspect output and cleanup status.'}
} finally {Pop-Location}
