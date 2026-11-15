<#
.SYNOPSIS
Refreshes NuGet lockfiles across the Windows solution after a reviewed dependency change.
#>
[CmdletBinding()]
param([switch] $Help)
if ($Help) { Get-Help $PSCommandPath; exit 0 }
. "$PSScriptRoot/common.ps1"
try {
    Invoke-TabriloDotnet @('restore', $TabriloProjects.Solution, '--force-evaluate', '-p:RestoreLockedMode=false')
    Invoke-TabriloDotnet @('restore', $TabriloProjects.Solution, '--locked-mode')
    Write-Output 'Review all changed package versions and checksums before committing the locks.'
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
