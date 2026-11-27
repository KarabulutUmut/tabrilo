<# Build and exercise an installed upgrade with an ephemeral, embedded test trust anchor. #>
[CmdletBinding()]
param(
    [ValidateSet('core','office')] [string] $OldFlavor = 'core',
    [ValidateSet('core','office')] [string] $NewFlavor = 'core',
    [version] $OldVersion = '0.1.99', [version] $NewVersion = '0.2.0'
)
. "$PSScriptRoot/common.ps1"
if ($OldVersion -ge $NewVersion) { throw 'The target must be newer.' }
Invoke-TabriloDotnet @('restore', $TabriloProjects.Solution, '--locked-mode')
$null = [Reflection.Assembly]::LoadFrom((Join-Path $TabriloRoot '.nuget/packages/netsparkleupdater.chaos.nacl/0.9.4/lib/net10.0/Chaos.NaCl.dll'))
$seed = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
[byte[]]$public = $null
[byte[]]$privateKey = $null
try {
    [Chaos.NaCl.Ed25519]::KeyPairFromSeed([ref]$public, [ref]$privateKey, $seed)
    $isolatedId = 'Tabrilo-Isolated-' + [guid]::NewGuid().ToString('N').Substring(0,12)
    function Build-Edition($version, $flavor) {
        $arguments = @{ Version = $version.ToString(); IsolatedTest = $true; IncludeOffice = ($flavor -eq 'office')
            IsolatedId = $isolatedId; IsolatedPublicKey = [Convert]::ToBase64String($public) }
        & "$PSScriptRoot/package.ps1" @arguments | Tee-Object -Variable packageLog | Out-Host
        if ($LASTEXITCODE) { throw 'Packaging failed.' }
        $result = @($packageLog | Where-Object { $_ -is [string] -and $_.StartsWith('Installer: ') })
        if ($result.Count -ne 1) { throw 'Expected exactly one installer path.' }
        Get-Item -LiteralPath $result[0].Substring(11)
    }
    $old = Build-Edition $OldVersion $OldFlavor
    $new = Build-Edition $NewVersion $NewFlavor
    $signature = [Convert]::ToBase64String([Chaos.NaCl.Ed25519]::Sign([IO.File]::ReadAllBytes($new.FullName), $privateKey))
    $digest = (Get-FileHash -LiteralPath $new.FullName).Hash
    $feed = Join-Path $new.DirectoryName 'appcast.xml'
    @"
<rss xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle"><channel><item>
  <sparkle:channel>isolated-test</sparkle:channel><sparkle:minimumSystemVersion>10.0.19041</sparkle:minimumSystemVersion>
  <enclosure url="https://tabrilo-install-test.invalid/setup.exe" length="$($new.Length)" flavor="$NewFlavor" sha256="$digest"
    sparkle:os="windows-x64" sparkle:version="$NewVersion" sparkle:edSignature="$signature" />
</item></channel></rss>
"@ | Set-Content -LiteralPath $feed -Encoding utf8NoBOM
    [Convert]::ToBase64String([Chaos.NaCl.Ed25519]::Sign([IO.File]::ReadAllBytes($feed), $privateKey)) |
        Set-Content -LiteralPath "$feed.signature" -Encoding ascii
} finally {
    if ($privateKey) { [Array]::Clear($privateKey) }
    [Array]::Clear($seed)
}
& "$PSScriptRoot/test.ps1" -OldInstaller $old.FullName -NewInstaller $new.FullName
exit $LASTEXITCODE
