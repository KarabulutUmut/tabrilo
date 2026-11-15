<#
.SYNOPSIS
Builds and runs the Windows integration executable and documentation checks.
.PARAMETER OldInstaller
Optional isolated-test installer path for the starting version.
.PARAMETER NewInstaller
Optional isolated-test installer path for the target version.
#>
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [string] $OldInstaller, [string] $NewInstaller, [switch] $Help)
if ($Help) { Get-Help $PSCommandPath; exit 0 }
. "$PSScriptRoot/common.ps1"
try {
    & "$PSScriptRoot/check-docs.ps1" -SelfTest
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & "$PSScriptRoot/build.ps1" -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $arguments = @('run', '--project', $TabriloProjects.Tests, '-c', $Configuration, '--no-build')
    if ($OldInstaller -or $NewInstaller) {
        if (!$OldInstaller -or !$NewInstaller) { throw 'Supply both isolated installer paths.' }
        $arguments += @('--', '--installed', (Resolve-Path -LiteralPath $OldInstaller).Path,
            (Resolve-Path -LiteralPath $NewInstaller).Path)
    }
    Invoke-TabriloDotnet $arguments
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
