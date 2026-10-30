param([ValidateSet('ReadOnly','Capture')][string]$Target='ReadOnly')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root 'artifacts/acceptance-tools'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$vs=& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$vs){throw 'Visual Studio C++ tools required.'}
$toolset=Get-ChildItem "$vs/VC/Tools/MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot="${env:ProgramFiles(x86)}/Windows Kits/10"
$sdk=Get-ChildItem "$sdkRoot/Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$env:INCLUDE="$($toolset.FullName)/include;$($sdk.FullName)/ucrt;$($sdk.FullName)/shared;$($sdk.FullName)/um"
$env:LIB="$($toolset.FullName)/lib/x64;$sdkRoot/Lib/$($sdk.Name)/ucrt/x64;$sdkRoot/Lib/$($sdk.Name)/um/x64"
$compiler=Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe'
Push-Location $out
try {
    $source=if($Target -eq 'ReadOnly'){'ReadOnlyAcceptance'}else{'CaptureHandoffIntegration'}
    $extra=@()
    if($Target -eq 'Capture'){
        & $compiler /nologo /c /O2 /MT /D_CRT_SECURE_NO_WARNINGS /D_WINSOCK_DEPRECATED_NO_WARNINGS "/I$root/native/include" "/I$root/third_party/ProxyBridge/src" "$root/third_party/ProxyBridge/src/pb_util.c" "$root/third_party/ProxyBridge/src/pb_socks5.c"
        if($LASTEXITCODE){throw 'Protocol helpers failed to compile.'}
        Set-Content -LiteralPath BuildVersion.h -Value '#define XA_VERSION "0.1.31-acceptance"' -Encoding ascii
        $extra=@("/FI$out/BuildVersion.h","/I$root/native/include","/I$root/third_party/ProxyBridge/src",'pb_util.obj','pb_socks5.obj')
    }
    & $compiler /nologo /std:c++20 /EHsc /O2 /MT /utf-8 /W3 /D_CRT_SECURE_NO_WARNINGS /D_WINSOCK_DEPRECATED_NO_WARNINGS @extra "$root/tests/WindowSocks.Native.PerformanceChecks/$source.cpp" "/Fe:$source.exe" /link ws2_32.lib iphlpapi.lib advapi32.lib
    if($LASTEXITCODE){throw 'Acceptance fixture build failed.'}
    Write-Output "Built only: $out\$source.exe. No live acceptance executed."
} finally {Pop-Location}
