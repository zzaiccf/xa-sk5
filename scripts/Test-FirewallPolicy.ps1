param([string]$Check = 'N2', [string]$OutputDirectory = 'artifacts/firewall-policy')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
New-Item -ItemType Directory -Path $out -Force | Out-Null
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$toolset = Get-ChildItem "$vs/VC/Tools/MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot = "${env:ProgramFiles(x86)}/Windows Kits/10"
$sdk = Get-ChildItem "$sdkRoot/Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$env:INCLUDE = "$($toolset.FullName)/include;$($sdk.FullName)/ucrt;$($sdk.FullName)/shared;$($sdk.FullName)/um"
$env:LIB = "$($toolset.FullName)/lib/x64;$sdkRoot/Lib/$($sdk.Name)/ucrt/x64;$sdkRoot/Lib/$($sdk.Name)/um/x64"
Push-Location $out
try {
    & "$($toolset.FullName)/bin/Hostx64/x64/cl.exe" /nologo /std:c++20 /EHsc /utf-8 /W3 "$root/tests/WindowSocks.Native.PerformanceChecks/FirewallChecks.cpp" /Fe:FirewallChecks.exe
    if ($LASTEXITCODE) { throw 'Firewall fixture compile failed.' }
    & .\FirewallChecks.exe $Check
    if ($LASTEXITCODE) { throw 'Firewall fixture failed.' }
} finally { Pop-Location }
