param([switch]$RunApproved,[switch]$GamesClosed,[string]$Endpoint,[int]$Port=0,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(!$RunApproved){Write-Output 'PLAN ONLY: with games closed, capture netsh filters before/after; run Test-SandboxFirewall.ps1 -RunApproved against the approved endpoint; verify test GUID cleanup. No action taken.';return}
if(!$GamesClosed){throw 'Confirm both games are closed with -GamesClosed after checking them.'}
if(!$Endpoint -or $Port -lt 1 -or $Port -gt 65535){throw 'Provide the user-approved reachable IPv4 endpoint and TCP port.'}
$root=Split-Path $PSScriptRoot -Parent
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts/acceptance/B-'+(Get-Date -Format yyyyMMdd-HHmmss))}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
function Snapshot-Filters([string]$Name){
    $file=Join-Path $OutputDirectory ($Name+'.xml')
    & netsh wfp show filters "file=$file" | Out-File -LiteralPath (Join-Path $OutputDirectory ($Name+'-netsh.txt'))
    if($LASTEXITCODE -or !(Test-Path -LiteralPath $file)){throw 'netsh filter snapshot failed.'}
    [xml]$xml=Get-Content -LiteralPath $file -Raw
    return ,$xml
}
function Product-Rows($Xml,[string]$Provider){
    @($Xml.SelectNodes("//*[local-name()='providerKey']") | Where-Object {$_.InnerText.Trim().Trim('{}') -ieq $Provider} | ForEach-Object {$_.ParentNode.OuterXml} | Sort-Object)
}
$test='eb758a61-7b8d-46d7-a4ef-918759818171';$product='783a5e91-5835-43ad-869e-6fcd5a310201'
$before=Snapshot-Filters 'before'
if(@(Product-Rows $before $test).Count){throw 'Preexisting test GUID filters found. Review them and use the documented test-only cleanup before retrying.'}
try {
    & (Join-Path $PSScriptRoot 'Test-SandboxFirewall.ps1') -RunApproved -Endpoint $Endpoint -Port $Port *> (Join-Path $OutputDirectory 'fixture.log')
} finally {
    $after=Snapshot-Filters 'after'
    $a=@(Product-Rows $before $product);$b=@(Product-Rows $after $product)
    Compare-Object -ReferenceObject $a -DifferenceObject $b | Out-File -LiteralPath (Join-Path $OutputDirectory 'product-filter-diff.txt')
    if(@(Product-Rows $after $test).Count){throw 'Test filters remain. Run WfpIntegration.exe --cleanup-test-approved, then repeat the read-only netsh snapshot. Never clear other providers.'}
}
Write-Output "B fixture completed. Inspect $OutputDirectory, including product-filter-diff.txt; background product activity can change that diff independently."
