<#
.SYNOPSIS
Restores locked dependencies and builds the Windows solution.
#>
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug', [switch] $Help)
if ($Help) { Get-Help $PSCommandPath; exit 0 }
. "$PSScriptRoot/common.ps1"
try {
    Invoke-TabriloDotnet @('restore', $TabriloProjects.Solution, '--locked-mode')
    Invoke-TabriloDotnet @('build', $TabriloProjects.Solution, '-c', $Configuration, '--no-restore')
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
