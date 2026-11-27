<# Inspect the pair of Windows review installers and write a local promotion input. #>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $CoreInstaller, [Parameter(Mandatory)] [string] $OfficeInstaller)
. "$PSScriptRoot/common.ps1"
$editions = [ordered]@{}
foreach ($entry in ([ordered]@{core=$CoreInstaller;office=$OfficeInstaller}).GetEnumerator()) {
    $setup = Get-Item -LiteralPath $entry.Value
    & "$PSScriptRoot/inspect-package.ps1" -Installer $setup.FullName | Out-Host
    if ($LASTEXITCODE) { throw "Inspection failed for $($entry.Key)." }
    $review = Get-Content (Join-Path $setup.DirectoryName 'review.json') -Raw | ConvertFrom-Json
    if ($review.identity.flavor -cne $entry.Key) { throw 'Supply one installer from each named edition.' }
    $editions[$entry.Key] = $review
}
foreach ($key in 'sourceCommit','version','channel','architecture','minimumBuild','os') {
    if ($editions.core.identity.$key -cne $editions.office.identity.$key) { throw "The two candidates differ in $key." }
}
$directory = New-Item -ItemType Directory -Force -Path "$TabriloRoot/artifacts/promotion/$($editions.core.identity.sourceCommit)"
$output = Join-Path $directory.FullName 'windows-review.json'
[ordered]@{schemaVersion=1;purpose='local-review';editions=$editions} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output
Write-Output "Windows promotion input: $output"
