<# Verify a local installer and its payload, producing review input without publishing anything. #>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Installer)
. "$PSScriptRoot/common.ps1"
$setup = Get-Item -LiteralPath $Installer
$payload = Join-Path $setup.DirectoryName 'payload'
if (!(Test-Json -LiteralPath "$payload/package-identity.json" -SchemaFile "$TabriloRoot/shared/contracts/windows-package.schema.json")) {
    throw 'The package identity does not match the Windows contract.'
}
$id = Get-Content "$payload/package-identity.json" -Raw | ConvertFrom-Json
$receipt = Get-Item "$payload/payload.receipt"
$digest = (Get-FileHash -LiteralPath $setup.FullName).Hash
$expected = (Get-Content -LiteralPath "$($setup.FullName).sha256" -Raw).Trim()
if ($expected -ne "$digest  $($setup.Name)" -or (Get-Content $receipt.FullName -TotalCount 1) -ne $id.flavor -or
    $setup.Name -notlike "*-$($id.version)-win-x64-$($id.flavor)-Setup.exe" -or $setup.VersionInfo.ProductVersion -ne $id.version) {
    throw 'The installer, identity, checksum or edition receipt differs.'
}
$check = Join-Path $setup.DirectoryName 'PayloadReceipt.exe'
& $check check $payload (Get-FileHash $receipt.FullName).Hash
if ($LASTEXITCODE) { throw 'The packaged files differ from the receipt.' }
$app = Get-Item "$payload/Tabrilo.exe"
$process = Start-Process $app.FullName -ArgumentList '--self-check' -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode) { throw "Application self-check failed ($($process.ExitCode))." }
$materials = Get-Content "$payload/licenses/materials.json" -Raw | ConvertFrom-Json -AsHashtable
$blocked = @($materials['windows-x64'][$id.flavor] | Where-Object { $materials.components[$_].status -eq 'blocked' })
$result = [ordered]@{ identity = $id; executable = $app.FullName; installer = $setup.FullName
    sha256 = $digest; installerBytes = $setup.Length
    payloadBytes = (Get-ChildItem -LiteralPath $payload -Recurse -File | Measure-Object Length -Sum).Sum
    receiptSha256 = (Get-FileHash $receipt.FullName).Hash; blockedMaterials = $blocked
    releaseNotesInput = "Review $($id.version), $($id.flavor), Windows x64 build 19041+, commit $($id.sourceCommit)."
    selfCheckExit = $process.ExitCode }
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $setup.DirectoryName 'review.json')
$result | ConvertTo-Json -Depth 5
