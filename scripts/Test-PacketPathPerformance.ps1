param([string]$OutputDirectory = 'artifacts/packet-path-checks')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = [IO.Path]::GetFullPath($OutputDirectory, $root)
New-Item -ItemType Directory -Path $out -Force | Out-Null
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ tools are required.' }
$toolset = Get-ChildItem "$vs/VC/Tools/MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot = "${env:ProgramFiles(x86)}/Windows Kits/10"
$sdk = Get-ChildItem "$sdkRoot/Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$env:INCLUDE = "$($toolset.FullName)/include;$($sdk.FullName)/ucrt;$($sdk.FullName)/shared;$($sdk.FullName)/um"
$env:LIB = "$($toolset.FullName)/lib/x64;$sdkRoot/Lib/$($sdk.Name)/ucrt/x64;$sdkRoot/Lib/$($sdk.Name)/um/x64"
$compiler = Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe'
Push-Location $out
try {
    $common = @('/nologo','/O2','/MT','/GS','/guard:cf','/utf-8','/W3','/D_CRT_SECURE_NO_WARNINGS','/D_WINSOCK_DEPRECATED_NO_WARNINGS',"/I$root/native/include","/I$root/third_party/ProxyBridge/src")
    & $compiler @common /c "$root/third_party/ProxyBridge/src/pb_util.c" "$root/third_party/ProxyBridge/src/pb_socks5.c"
    if ($LASTEXITCODE) { throw 'Protocol helpers failed to compile.' }
    & $compiler @common /std:c++20 /EHsc "$root/tests/WindowSocks.Native.PerformanceChecks/PacketPathChecks.cpp" /Fe:PacketPathChecks.exe /link pb_util.obj pb_socks5.obj ws2_32.lib iphlpapi.lib advapi32.lib
    if ($LASTEXITCODE) { throw 'Packet checks failed to compile.' }
    & ./PacketPathChecks.exe "$root/third_party/WinDivert-2.2.2-A/x64/WinDivert.dll" | Tee-Object -FilePath result.txt
    if ($LASTEXITCODE) { throw 'Packet-path regression failed.' }
} finally { Pop-Location }
