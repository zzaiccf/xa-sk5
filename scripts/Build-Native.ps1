$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repoRoot 'artifacts/native'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsPath) { throw 'Visual Studio C++ tools are required.' }
$toolset = Get-ChildItem "$vsPath/VC/Tools/MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot = "${env:ProgramFiles(x86)}/Windows Kits/10"
$sdk = Get-ChildItem "$sdkRoot/Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$compiler = Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe'
$linker = Join-Path $toolset.FullName 'bin/Hostx64/x64/link.exe'
$env:INCLUDE = "$($toolset.FullName)/include;$($sdk.FullName)/ucrt;$($sdk.FullName)/shared;$($sdk.FullName)/um"
$env:LIB = "$($toolset.FullName)/lib/x64;$sdkRoot/Lib/$($sdk.Name)/ucrt/x64;$sdkRoot/Lib/$($sdk.Name)/um/x64"
Push-Location $out
try {
    $common = @('/nologo','/c','/O2','/MT','/GS','/guard:cf','/utf-8','/W3','/D_CRT_SECURE_NO_WARNINGS','/D_WINSOCK_DEPRECATED_NO_WARNINGS',"/I$repoRoot/native/include", "/I$repoRoot/third_party/ProxyBridge/src")
    & $compiler @common "$repoRoot/third_party/ProxyBridge/src/pb_util.c" "$repoRoot/third_party/ProxyBridge/src/pb_socks5.c"
    if ($LASTEXITCODE) { throw 'Native protocol compilation failed.' }
    & $compiler @common '/std:c++20' '/EHsc' "$repoRoot/native/WindowSocksNative.cpp"
    if ($LASTEXITCODE) { throw 'Native engine compilation failed.' }
    & $linker /NOLOGO /DLL /OUT:WindowSocksNative.dll /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /guard:cf WindowSocksNative.obj pb_util.obj pb_socks5.obj ws2_32.lib iphlpapi.lib advapi32.lib
    if ($LASTEXITCODE) { throw 'Native linking failed.' }
} finally { Pop-Location }
