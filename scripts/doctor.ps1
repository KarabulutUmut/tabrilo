<#
.SYNOPSIS
Reports Tabrilo prerequisites and desktop-session evidence.
#>
[CmdletBinding()]
param([switch] $Help)
if ($Help) { Get-Help $PSCommandPath; exit 0 }
. "$PSScriptRoot/common.ps1"

$os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
Write-Output "$($os.ProductName) $($os.DisplayVersion) build $($os.CurrentBuild).$($os.UBR)"
Write-Output "OS architecture $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
Write-Output "PowerShell $($PSVersionTable.PSVersion)"
Write-Output "Session $((Get-Process -Id $PID).SessionId), interactive flag $([Environment]::UserInteractive)"
$nsis = Join-Path $TabriloRoot '.tools/nsis-unpacked/nsis-3.13/makensis.exe'
if (!(Test-Path $nsis)) { $nsis = (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source }
if ($nsis) { & $nsis /VERSION } else { Write-Output 'NSIS 3.13 is required for packaging.' }
$qpdf = Join-Path $TabriloRoot '.tools/qpdf/qpdf-12.4.2-msvc64/bin/qpdf.exe'
if (Test-Path -LiteralPath $qpdf) { Write-Output "qpdf staged at $qpdf" }
else { Write-Output 'qpdf is missing. Run scripts/restore-tools.ps1 before building.' }
try {
    Push-Location $TabriloRoot
    try { $version = & (Get-TabriloDotnet) --version }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0 -or $version -ne $TabriloSdk) { throw "Required SDK $TabriloSdk is unavailable." }
    Write-Output ".NET SDK $version"
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
