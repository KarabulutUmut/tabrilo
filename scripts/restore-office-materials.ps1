<# Cache and verify the source release and its upstream build-input archive list for the Office edition. #>
[CmdletBinding()]
param([switch] $External)
. "$PSScriptRoot/common.ps1"
$source = Get-Content "$TabriloRoot/packaging/licenses/LibreOffice-sources.json" -Raw | ConvertFrom-Json
$items = @($source.archives)
if ($External) {
    $record = Get-Content "$TabriloRoot/packaging/licenses/LibreOffice-external.json" -Raw | ConvertFrom-Json
    if ($record.sourceCommit -cne $source.sourceCommit) { throw 'Source releases differ.' }
    $items += $record.archives
}
$output = (New-Item -ItemType Directory -Force -Path "$TabriloRoot/artifacts/materials/libreoffice-$($source.version)").FullName
$results = $items | ForEach-Object -ThrottleLimit 4 -Parallel {
    $uri = [uri]$_.url
    $name = $uri.Segments[-1]
    if ($uri.Scheme -ne 'https' -or $uri.Host -notin 'download.documentfoundation.org','dev-www.libreoffice.org' -or
        $name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_.+-]+$' -or $_.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid source inventory entry.' }
    $file = Join-Path $using:output $name
    $temp = "$file.$([guid]::NewGuid().ToString('N')).partial"
    try {
        if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file).Hash -ne $_.sha256) {
            & curl.exe --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --connect-timeout 20 --retry 1 --max-time 480 --output $temp $uri.AbsoluteUri
            if ($LASTEXITCODE) { throw "Download exited $LASTEXITCODE." }
            if ((Get-FileHash -LiteralPath $temp).Hash -ne $_.sha256) { throw 'Checksum differs.' }
            [IO.File]::Move($temp, $file, $true)
        }
        [ordered]@{name=$name;sha256=$_.sha256;bytes=(Get-Item -LiteralPath $file).Length;verified=$true}
    } catch { [ordered]@{name=$name;verified=$false;problem=$_.Exception.Message} }
    finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
}
$results | ConvertTo-Json -Depth 3 | Set-Content "$output/verification.json"
$failed = @($results | Where-Object { !$_.verified })
if ($results.Count -ne $items.Count -or $failed.Count) { throw "$($failed.Count) sources unavailable or invalid. See $output/verification.json." }
Write-Output "Verified $($results.Count) upstream archives at $output."
