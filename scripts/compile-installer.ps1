function New-TabriloInstaller([string] $payload, [string] $output, [string] $version, [string] $kind, [bool] $includeOffice) {
    $nsis = Join-Path $TabriloRoot '.tools/nsis-unpacked/nsis-3.13/makensis.exe'
    $licenses = Join-Path $payload 'licenses'
    if ((& $nsis /VERSION) -ne 'v3.13') { throw 'The pinned NSIS compiler is not available.' }
    Copy-Item -LiteralPath (Join-Path (Split-Path $nsis -Parent) 'COPYING') -Destination (Join-Path $licenses 'NSIS-COPYING.txt')
    $helper = Join-Path $output 'PayloadReceipt.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    & $compiler /nologo /optimize+ /target:exe "/out:$helper" (Join-Path $TabriloRoot 'packaging/windows/PayloadReceipt.cs')
    if ($LASTEXITCODE) { throw 'Could not build the payload receipt verifier.' }
    $receipt = Join-Path $payload 'payload.receipt'
    @(
        if ($IncludeOffice) { 'office' } else { 'core' }
        foreach ($entry in (Get-ChildItem -LiteralPath $payload -Recurse | Sort-Object FullName)) {
            $relative = [IO.Path]::GetRelativePath($payload, $entry.FullName)
            $hash = if ($entry.PSIsContainer) { 'dir' } else { (Get-FileHash -LiteralPath $entry.FullName).Hash }
            "$hash`t$relative"
        }
    ) | Set-Content -LiteralPath $receipt -Encoding utf8
    $receiptHash = (Get-FileHash -LiteralPath $receipt).Hash
    $files = Get-ChildItem -LiteralPath $payload -Recurse -File
    $directories = Get-ChildItem -LiteralPath $payload -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending
    $manifest = Join-Path $output 'Payload.nsh'
    @(
        '!macro RemovePayload ROOT'
        foreach ($file in $files) {
            $leaf = [IO.Path]::GetRelativePath($payload, $file.FullName)
            '  Delete "${ROOT}\' + $leaf.Replace('$', '$$') + '"'
        }
        foreach ($directory in $directories) {
            $leaf = [IO.Path]::GetRelativePath($payload, $directory.FullName)
            '  RMDir "${ROOT}\' + $leaf.Replace('$', '$$') + '"'
        }
        '  RMDir "${ROOT}"'
        '!macroend'
    ) | Set-Content -LiteralPath $manifest -Encoding utf8BOM

    $flavor = if ($includeOffice) { 'office' } else { 'core' }
    $installer = Join-Path $output "$kind-$Version-win-x64-$flavor-Setup.exe"
    $arguments = @('/V3', "/DPRODUCT_ID=$kind", "/DAPP_VERSION=$Version", "/DPAYLOAD=$payload",
        "/DOUTPUT=$installer", "/DDELETE_MANIFEST=$manifest", "/DRECEIPT_HASH=$receiptHash",
        "/DRECEIPT_HELPER=$helper", "/DFLAVOR=$flavor", (Join-Path $TabriloRoot 'packaging/windows/Tabrilo.nsi'))
    & $nsis @arguments
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $installer)) { throw "NSIS failed with exit $LASTEXITCODE." }
    $digest = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$digest  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    Write-Output "Installer: $installer"
}
