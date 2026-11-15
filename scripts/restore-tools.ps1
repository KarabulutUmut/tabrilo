<#
.SYNOPSIS
Downloads checksum-pinned portable build assets into this workspace.
.PARAMETER Installer
Also downloads the portable NSIS compiler.
.PARAMETER Validation
Also downloads actionlint for repository validation.
#>
[CmdletBinding()]
param([switch] $Installer, [switch] $Validation, [switch] $Help)
if ($Help) { Get-Help $PSCommandPath; exit 0 }
. "$PSScriptRoot/common.ps1"

function Expand-VerifiedAsset([string] $Origin, [string] $Digest, [string] $Archive, [string] $Destination) {
    if (!(Test-Path -LiteralPath $Archive)) {
        $temporary = $Archive + '.download'
        try {
            Invoke-WebRequest $Origin -OutFile $temporary
            if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $Digest) {
                throw "Downloaded asset checksum does not match: $Origin"
            }
            [IO.File]::Move($temporary, $Archive, $false)
        } finally { [IO.File]::Delete($temporary) }
    }
    if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $Digest) {
        throw "Cached asset checksum does not match: $Archive"
    }
    if (!(Test-Path -LiteralPath $Destination)) {
        $unpacked = "$Destination-$([guid]::NewGuid().ToString('N'))"
        [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $unpacked)
        [IO.Directory]::Move($unpacked, $Destination)
    }
    Write-Output "Verified $Archive ($Digest)"
}

try {
    $tools = Join-Path $TabriloRoot '.tools'
    $null = New-Item -ItemType Directory -Path $tools -Force
    $pins = (Get-Content (Join-Path $TabriloRoot 'shared/dependencies.json') -Raw | ConvertFrom-Json).assets
    Expand-VerifiedAsset $pins.qpdf.url $pins.qpdf.sha256 (Join-Path $tools 'qpdf.zip') (Join-Path $tools 'qpdf')
    if ($Validation) {
        Expand-VerifiedAsset $pins.actionlint.url $pins.actionlint.sha256 (Join-Path $tools 'actionlint.zip') (Join-Path $tools 'actionlint')
    }
    if ($Installer) {
        $nsisZip = Join-Path $tools 'nsis-3.13.zip'
        $origin = $pins.nsis.url
        if (!(Test-Path -LiteralPath $nsisZip)) {
            $landing = (Invoke-WebRequest $origin).Content
            $download = [regex]::Match($landing, 'https://downloads\.sourceforge\.net/project/nsis/NSIS%203/3\.13/nsis-3\.13\.zip\?[^"''<> ]+')
            if (!$download.Success) { throw 'The upstream NSIS download link is unavailable.' }
            $origin = [Net.WebUtility]::HtmlDecode($download.Value)
        }
        Expand-VerifiedAsset $origin $pins.nsis.sha256 $nsisZip (Join-Path $tools 'nsis-unpacked')
    }
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
