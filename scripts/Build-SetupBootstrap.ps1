param([Parameter(Mandatory)][string]$HostExecutable, [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$hostPath=(Resolve-Path -LiteralPath $HostExecutable).Path
$output=[IO.Path]::GetFullPath($OutputPath,$repoRoot)
if($hostPath -eq $output){throw 'The native launcher must not overwrite its embedded host.'}
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
$build=Join-Path (Split-Path $output -Parent) ('bootstrap-build-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $build | Out-Null
$version=([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$parts=$version.Split('.')+[string]'0'
$vsWhere="${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath=& $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $vsPath){throw 'Visual Studio C++ tools are required.'}
$toolset=Get-ChildItem "$vsPath/VC/Tools/MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot="${env:ProgramFiles(x86)}/Windows Kits/10"
$sdk=Get-ChildItem "$sdkRoot/Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1
$compiler=Join-Path $toolset.FullName 'bin/Hostx64/x64/cl.exe'
$linker=Join-Path $toolset.FullName 'bin/Hostx64/x64/link.exe'
$rc=Join-Path $sdkRoot "bin/$($sdk.Name)/x64/rc.exe"
$env:INCLUDE="$($toolset.FullName)/include;$($sdk.FullName)/ucrt;$($sdk.FullName)/shared;$($sdk.FullName)/um"
$env:LIB="$($toolset.FullName)/lib/x64;$sdkRoot/Lib/$($sdk.Name)/ucrt/x64;$sdkRoot/Lib/$($sdk.Name)/um/x64"
$icon=(Join-Path $repoRoot 'src/WindowSocks.Desktop/Assets/Brand/xa-sk5.ico').Replace('\','\\')
$resourceHost=$hostPath.Replace('\','\\')
$manifest=Join-Path $build 'bootstrap.manifest'
$manifestText=[IO.File]::ReadAllText((Join-Path $repoRoot 'src/WindowSocks.Setup/app.manifest')).Replace('</assembly>','<dependency><dependentAssembly><assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*"/></dependentAssembly></dependency></assembly>')
[IO.File]::WriteAllText($manifest,$manifestText,[Text.UTF8Encoding]::new($false))
$resource=@"
#include <windows.h>
101 ICON "$icon"
201 RCDATA "$resourceHost"
1 RT_MANIFEST "$($manifest.Replace('\','\\'))"
1 VERSIONINFO
 FILEVERSION $($parts -join ',')
 PRODUCTVERSION $($parts -join ',')
 FILEOS VOS_NT_WINDOWS32
 FILETYPE VFT_APP
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904b0"
  BEGIN
   VALUE "FileDescription", "xa sk5 Setup"
   VALUE "FileVersion", "$version"
   VALUE "ProductName", "xa sk5"
   VALUE "ProductVersion", "$version"
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x409, 1200
 END
END
"@
[IO.File]::WriteAllText((Join-Path $build 'bootstrap.rc'),$resource,[Text.UTF8Encoding]::new($false))
Push-Location $build
try {
 & $rc /nologo /fo bootstrap.res bootstrap.rc
 if($LASTEXITCODE){throw 'Installer resource compilation failed.'}
 & $compiler /nologo /c /std:c++20 /EHsc /O2 /MT /GS /guard:cf /utf-8 /W4 /FoSetupBootstrap.obj (Join-Path $repoRoot 'native/SetupBootstrap.cpp')
 if($LASTEXITCODE){throw 'Native installer launcher compilation failed.'}
 & $linker /NOLOGO /SUBSYSTEM:WINDOWS /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /guard:cf /MANIFEST:NO (('/OUT:'+$output)) SetupBootstrap.obj bootstrap.res user32.lib gdi32.lib shell32.lib ole32.lib advapi32.lib comctl32.lib
 if($LASTEXITCODE){throw 'Native installer launcher linking failed.'}
} finally {Pop-Location}
Write-Host "Native preparation launcher ready: $output"
