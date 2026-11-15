<#
.SYNOPSIS
Builds and starts the native Windows app.
#>
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug', [switch] $Help)
if ($Help) { Get-Help $PSCommandPath; exit 0 }
. "$PSScriptRoot/common.ps1"
try {
    & "$PSScriptRoot/build.ps1" -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Invoke-TabriloDotnet @('run', '--project', $TabriloProjects.App, '-c', $Configuration, '--no-build')
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
