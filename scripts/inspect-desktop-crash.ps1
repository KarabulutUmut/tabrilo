<# Read a synthetic test crash on the disposable runner. Only stack frames enter the log. #>
[CmdletBinding()]
param([Parameter(Mandatory,ParameterSetName='dump')][string]$Dump,
    [Parameter(Mandatory,ParameterSetName='replay')][string]$Image,
    [Parameter(Mandatory,ParameterSetName='replay')][string]$Document)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Requires a disposable hosted runner.' }
$cdb = "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\cdb.exe"
if (!(Test-Path -LiteralPath $cdb)) {
    $work = (New-Item -ItemType Directory -Force -Path "$env:RUNNER_TEMP/TabriloStack").FullName
    $bundle = Join-Path $work 'windbg.msixbundle'
    Invoke-WebRequest 'https://windbg.download.prss.microsoft.com/dbazure/prod/1-2611-4001-0/windbg.msixbundle' -OutFile $bundle
    if ((Get-FileHash $bundle).Hash -cne '158F6365F365A939429977F7BC4CA3D15F8C2A39BC3ACD2B297FC36A31D69274') { throw 'Debugger checksum mismatch.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($bundle)
    $msix = Join-Path $work 'windbg_win-x64.msix'
    try { [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('windbg_win-x64.msix'),$msix,$true) }
    finally { $archive.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($msix,$work,$true)
    $cdb = Join-Path $work 'amd64/cdb.exe'
}
$symbols = "srv*$env:RUNNER_TEMP/TabriloSymbols*https://msdl.microsoft.com/download/symbols"
if ($Dump) { $arguments = @('-z',$Dump,'-c','.ecxr; k 32; q') }
else {
    $hostExe = (Resolve-Path "$PSScriptRoot/../tests/windows/Tabrilo.DesktopTests/bin/Release/net10.0-windows10.0.19041.0/Tabrilo.DesktopTests.exe").Path
    $arguments = @('-G','-c','sxe -c "k 32; gn" -c2 "k 32; gn" 0xc000000d; g',$hostExe,$Image,$Document)
}
& $cdb -y $symbols @arguments |
    Select-String 'Child-SP|RetAddr|[a-zA-Z_0-9]+![a-zA-Z_?~:0-9]+|\+0x[0-9a-f]+' | ForEach-Object Line
