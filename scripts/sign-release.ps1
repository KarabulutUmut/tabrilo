<#
.SYNOPSIS
Authenticode-signs a reviewed Windows installer and produces a strictly signed appcast.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Installer,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $ApprovedCommit
)
. "$PSScriptRoot/common.ps1"

try {
    if ($env:TABRILO_WINDOWS_RELEASE_ENABLED -ne 'true') { throw 'The reviewed Windows release environment is disabled.' }
    $setup = Get-Item -LiteralPath $Installer
    $package = Get-Content -Raw (Join-Path $setup.DirectoryName 'payload/package-identity.json') | ConvertFrom-Json
    $materials = Get-Content -Raw "$TabriloRoot/packaging/materials.json" | ConvertFrom-Json -AsHashtable
    if ($package.sourceCommit -cne $ApprovedCommit -or (git -C $TabriloRoot rev-parse HEAD) -cne $ApprovedCommit -or
        (git -C $TabriloRoot status --porcelain) -or $package.flavor -notin 'core','office' -or $package.channel -ne 'stable' -or
        $env:TABRILO_APPROVED_RELEASE_COMMIT -cne $ApprovedCommit) { throw 'An approved clean stable candidate is required.' }
    foreach ($component in $materials['windows-x64'][$package.flavor]) {
        if ($materials.components[$component].status -ne 'approved') { throw "Distribution gate is open for $component." }
    }
    & "$PSScriptRoot/inspect-package.ps1" -Installer $setup.FullName
    if ($LASTEXITCODE) { throw 'Package inspection failed.' }
    if (!$env:TABRILO_WINDOWS_SIGN_SEED -or !$env:TABRILO_WINDOWS_SIGN_PFX -or
        !$env:TABRILO_WINDOWS_SIGN_PFX_PASSWORD -or !$env:TABRILO_WINDOWS_TIMESTAMP_SERVER) {
        throw 'The production Windows signing configuration is incomplete.'
    }
    $config = Get-Content (Join-Path $TabriloRoot 'shared/contracts/windows-update.json') -Raw | ConvertFrom-Json
    if ($config.channel -ne 'stable' -or $config.platform -ne 'windows-x64' -or
        $config.feed -notmatch '^https://' -or !$config.publicKey -or $env:TABRILO_WINDOWS_TIMESTAMP_SERVER -notmatch '^https://') {
        throw 'The production HTTPS update channel and trust anchor must be reviewed and configured.'
    }
    if ($config.installerOrigin -ne 'https://github.com' -or
        $config.installerRedirectOrigin -ne 'https://release-assets.githubusercontent.com' -or
        $env:GITHUB_REPOSITORY -ne 'KarabulutUmut/tabrilo') {
        throw 'Check the approved repository and installer origins.'
    }
    $setup = Get-Item -LiteralPath $Installer
    if ($setup.Name -ne "Tabrilo-$Version-win-x64-$($package.flavor)-Setup.exe" -or $setup.VersionInfo.ProductVersion -ne $Version) {
        throw 'The installer filename or embedded version is invalid.'
    }

    $packages = Join-Path $TabriloRoot '.nuget/packages'
    $null = [Reflection.Assembly]::LoadFrom((Join-Path $packages 'netsparkleupdater.chaos.nacl/0.9.4/lib/net10.0/Chaos.NaCl.dll'))
    $null = [Reflection.Assembly]::LoadFrom((Join-Path $packages 'netsparkleupdater.sparkleupdater/3.1.0/lib/net10.0/NetSparkle.dll'))
    $seed = [Convert]::FromBase64String($env:TABRILO_WINDOWS_SIGN_SEED)
    $env:TABRILO_WINDOWS_SIGN_SEED = $null
    [byte[]]$public = $null
    [byte[]]$expanded = $null
    $certificate = $null
    try {
        if ($seed.Length -ne 32) { throw 'The Ed25519 seed is invalid.' }
        [Chaos.NaCl.Ed25519]::KeyPairFromSeed([ref]$public, [ref]$expanded, $seed)
        if ([Convert]::ToBase64String($public) -cne $config.publicKey) { throw 'The signing key does not match the Windows trust anchor.' }
        $pfx = [Convert]::FromBase64String($env:TABRILO_WINDOWS_SIGN_PFX)
        $env:TABRILO_WINDOWS_SIGN_PFX = $null
        try {
            $certificate = [Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12(
                $pfx, $env:TABRILO_WINDOWS_SIGN_PFX_PASSWORD,
                [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet)
        } finally {
            [Array]::Clear($pfx)
            $env:TABRILO_WINDOWS_SIGN_PFX_PASSWORD = $null
        }
        if (!$certificate.HasPrivateKey) { throw 'The Windows publisher certificate has no signing key.' }
        $options = @{ LiteralPath = $setup.FullName; Certificate = $certificate; HashAlgorithm = 'SHA256'
            TimestampServer = $env:TABRILO_WINDOWS_TIMESTAMP_SERVER }
        $result = Set-AuthenticodeSignature @options
        if ($result.Status -ne 'Valid' -or !$result.TimeStamperCertificate) { throw 'Authenticode signing or timestamp validation failed.' }
        $setup.Refresh()
        $signature = [Convert]::ToBase64String([Chaos.NaCl.Ed25519]::Sign([IO.File]::ReadAllBytes($setup.FullName), $expanded))
        $validator = [NetSparkleUpdater.SignatureVerifiers.Ed25519Checker]::new(
            [NetSparkleUpdater.Enums.SecurityMode]::Strict, $config.publicKey, $null, $false, 8192)
        if ($validator.VerifySignatureOfFile($signature, $setup.FullName).ToString() -ne 'Valid') {
            throw 'Strict installer signature verification failed.'
        }
        $appcast = Join-Path $setup.DirectoryName "appcast-$($package.flavor).xml"
        $asset = "https://github.com/$env:GITHUB_REPOSITORY/releases/download/windows-x64-v$Version/$($setup.Name)"
        @"
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>Tabrilo Windows x64 stable</title>
    <item>
      <title>Tabrilo $Version</title>
      <sparkle:channel>stable</sparkle:channel>
      <sparkle:minimumSystemVersion>10.0.19041</sparkle:minimumSystemVersion>
      <enclosure url="$asset" type="application/octet-stream" length="$($setup.Length)"
        flavor="$($package.flavor)" sha256="$((Get-FileHash -LiteralPath $setup.FullName).Hash)"
        sparkle:os="windows-x64" sparkle:version="$Version" sparkle:edSignature="$signature" />
    </item>
  </channel>
</rss>
"@ | Set-Content -LiteralPath $appcast -Encoding utf8NoBOM
        $feedSignature = [Convert]::ToBase64String([Chaos.NaCl.Ed25519]::Sign([IO.File]::ReadAllBytes($appcast), $expanded))
        if ($validator.VerifySignatureOfFile($feedSignature, $appcast).ToString() -ne 'Valid') { throw 'Strict appcast signature verification failed.' }
        $feedSignature | Set-Content -LiteralPath "$appcast.signature" -Encoding ascii
        $digest = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$digest  $($setup.Name)" | Set-Content -LiteralPath "$($setup.FullName).sha256" -Encoding ascii
        Write-Output 'Installer and Windows x64 appcast verified.'
    } finally {
        if ($certificate) { $certificate.Dispose() }
        if ($expanded) { [Array]::Clear($expanded) }
        [Array]::Clear($seed)
    }
    exit 0
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
