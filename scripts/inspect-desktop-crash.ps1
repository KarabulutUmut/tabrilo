<# Read a synthetic test crash on the disposable runner. Only stack frames enter the log. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Image, [Parameter(Mandatory)][string]$Document)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Requires a disposable hosted runner.' }
$cdb = "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\cdb.exe"
if (!(Test-Path -LiteralPath $cdb)) {
    Write-Warning 'The Windows SDK debugger is unavailable for an additional native stack.'
    return
}
$symbols = "srv*$env:RUNNER_TEMP/TabriloSymbols*https://msdl.microsoft.com/download/symbols"
$hostExe = (Resolve-Path "$PSScriptRoot/../tests/windows/Tabrilo.DesktopTests/bin/Release/net10.0-windows10.0.19041.0/Tabrilo.DesktopTests.exe").Path
$arguments = @('-G','-c','sxe -c "k 32; gn" -c2 "k 32; gn" 0xc000000d; g',$hostExe,$Image,$Document)
& $cdb -y $symbols @arguments |
    Select-String 'Child-SP|RetAddr|[a-zA-Z_0-9]+![a-zA-Z_?~:0-9]+|\+0x[0-9a-f]+' | ForEach-Object Line
