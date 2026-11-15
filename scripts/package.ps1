<#
.SYNOPSIS
Publishes a self-contained Windows x64 payload and builds a local per-user NSIS installer.
.PARAMETER Configuration
Build configuration. Defaults to Release.
.PARAMETER Version
Three-part build version. Defaults to 0.1.0.
.PARAMETER PayloadOnly
Publish the self-contained runnable payload without compiling an installer.
.PARAMETER IsolatedTest
Use a separate per-user test identity and installation directory.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')] [string] $Configuration = 'Release',
    [ValidatePattern('^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$')] [string] $Version = '0.1.0',
    [switch] $PayloadOnly, [switch] $IsolatedTest, [switch] $Help
)
if ($Help) { Get-Help $PSCommandPath; exit 0 }
. "$PSScriptRoot/common.ps1"
try {
    if (@($Version.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count) { throw 'Version parts must be at most 65535.' }
    $qpdf = Join-Path $TabriloRoot '.tools/qpdf/qpdf-12.4.2-msvc64/bin/qpdf.exe'
    $nsis = Join-Path $TabriloRoot '.tools/nsis-unpacked/nsis-3.13/makensis.exe'
    if (!(Test-Path -LiteralPath $qpdf)) { throw 'Restore qpdf with scripts/restore-tools.ps1.' }
    if (!$PayloadOnly -and !(Test-Path -LiteralPath $nsis)) { throw 'Restore NSIS with scripts/restore-tools.ps1 -Installer.' }
    $channel = if ($IsolatedTest) { 'isolated-test' } else { 'local' }
    $run = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $output = Join-Path $TabriloRoot "artifacts/package/$channel/$Version/$run"
    $payload = Join-Path $output 'payload'
    $null = New-Item -ItemType Directory -Path $output -Force
    Invoke-TabriloDotnet @('restore', $TabriloProjects.Solution, '--locked-mode')
    Invoke-TabriloDotnet @('publish', $TabriloProjects.App, '-c', $Configuration, '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishTrimmed=false', '--no-restore', "-p:Version=$Version",
        "-p:TabriloInstallIdentity=$(if ($IsolatedTest) {'Tabrilo-Isolated'} else {'Tabrilo'})", '-o', $payload)

    $licenses = Join-Path $payload 'licenses'
    $null = New-Item -ItemType Directory -Path $licenses
    Copy-Item -LiteralPath (Join-Path $TabriloRoot 'LICENSE'),(Join-Path $TabriloRoot 'THIRD_PARTY_NOTICES.txt') -Destination $licenses
    Copy-Item -LiteralPath (Join-Path $TabriloRoot 'packaging/licenses') -Destination (Join-Path $licenses 'Upstream') -Recurse
    $packages = Join-Path $TabriloRoot '.nuget/packages'
    $lock = Get-Content (Join-Path $TabriloRoot 'apps/windows/Tabrilo.Windows/packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($framework in $lock.dependencies.Values) {
        foreach ($dependency in $framework.GetEnumerator()) {
            if (!$dependency.Value.resolved) { continue }
            $id = $dependency.Key.ToLowerInvariant()
            $versionRoot = Join-Path $packages "$id/$($dependency.Value.resolved)"
            Get-ChildItem -LiteralPath $versionRoot -File | Where-Object Name -Match '^(license|copyright|notice|third-party-notices)(\.|$)' |
                ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenses "$id-$($_.Name)") }
        }
    }
    foreach ($runtime in 'microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64') {
        $runtimeRoot = Join-Path $packages "$runtime/10.0.15"
        Get-ChildItem -LiteralPath $runtimeRoot -File | Where-Object Name -Match '^(license|third-party-notices)(\.|$)' |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenses "$runtime-$($_.Name)") }
    }
    if (!(Test-Path -LiteralPath (Join-Path $payload 'Tabrilo.exe')) -or
        !(Test-Path -LiteralPath (Join-Path $payload 'tools/qpdf/qpdf.exe'))) {
        throw 'The payload is incomplete.'
    }
    Write-Output "Application: $(Join-Path $payload 'Tabrilo.exe')"
    if ($PayloadOnly) { exit 0 }

    if ((& $nsis /VERSION) -ne 'v3.13') { throw 'The pinned NSIS compiler is not available.' }
    Copy-Item -LiteralPath (Join-Path (Split-Path $nsis -Parent) 'COPYING') -Destination (Join-Path $licenses 'NSIS-COPYING.txt')
    $files = Get-ChildItem -LiteralPath $payload -Recurse -File
    $directories = Get-ChildItem -LiteralPath $payload -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending
    $manifest = Join-Path $output 'Payload.nsh'
    @(
        '!macro RemovePayload ROOT'
        foreach ($file in $files) {
            $leaf = [IO.Path]::GetRelativePath($payload, $file.FullName)
            '  Delete "${ROOT}\' + $leaf + '"'
        }
        foreach ($directory in $directories) {
            $leaf = [IO.Path]::GetRelativePath($payload, $directory.FullName)
            '  RMDir "${ROOT}\' + $leaf + '"'
        }
        '  RMDir "${ROOT}"'
        '!macroend'
    ) | Set-Content -LiteralPath $manifest -Encoding utf8
    $kind = if ($IsolatedTest) { 'Tabrilo-Isolated' } else { 'Tabrilo' }
    $installer = Join-Path $output "$kind-$Version-win-x64-Setup.exe"
    $arguments = @('/V3', "/DPRODUCT_ID=$kind", "/DAPP_VERSION=$Version", "/DPAYLOAD=$payload",
        "/DOUTPUT=$installer", "/DDELETE_MANIFEST=$manifest", (Join-Path $TabriloRoot 'packaging/windows/Tabrilo.nsi'))
    & $nsis @arguments
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $installer)) { throw "NSIS failed with exit $LASTEXITCODE." }
    $digest = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$digest  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    Write-Output "Installer: $installer"
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
