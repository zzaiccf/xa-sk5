$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$commit = 'd697164b836f0d3f897128774d3cb72e54770e74'
$destination = Join-Path $repoRoot 'third_party/ProxyBridge'
New-Item -ItemType Directory -Path "$destination/src" -Force | Out-Null
$tree = Invoke-RestMethod "https://api.github.com/repos/InterceptSuite/ProxyBridge/git/trees/$commit`?recursive=1"
foreach ($entry in $tree.tree | Where-Object { $_.path -match '^Windows/src/[^/]+\.(c|h)$' }) {
    $target = Join-Path "$destination/src" (Split-Path $entry.path -Leaf)
    if (!(Test-Path -LiteralPath $target)) {
        Invoke-WebRequest "https://raw.githubusercontent.com/InterceptSuite/ProxyBridge/$commit/$($entry.path)" -OutFile $target
    }
}
foreach ($file in @('LICENSE', 'README.md')) {
    if (!(Test-Path -LiteralPath "$destination/$file")) {
        Invoke-WebRequest "https://raw.githubusercontent.com/InterceptSuite/ProxyBridge/$commit/$file" -OutFile "$destination/$file"
    }
}
$commit | Set-Content "$destination/UPSTREAM_COMMIT" -Encoding utf8
# Fetch source/header/legal text only. Driver acquisition is an explicit manual prerequisite.
$include = Join-Path $repoRoot 'native/include'
New-Item -ItemType Directory -Path $include -Force | Out-Null
if (!(Test-Path "$include/windivert.h")) { Invoke-WebRequest 'https://raw.githubusercontent.com/basil00/WinDivert/v2.2.2/include/windivert.h' -OutFile "$include/windivert.h" }
if (!(Test-Path "$include/WinDivert-LICENSE.txt")) { Invoke-WebRequest 'https://raw.githubusercontent.com/basil00/WinDivert/v2.2.2/LICENSE' -OutFile "$include/WinDivert-LICENSE.txt" }
Write-Host 'Pinned sources ready. No driver binaries downloaded or security settings changed.'
