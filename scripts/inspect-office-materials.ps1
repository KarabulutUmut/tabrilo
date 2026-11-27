<# Compare an administrative Office image with the pinned upstream MSI and index its license sections. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OfficeDirectory, [string]$Installer)
. "$PSScriptRoot/common.ps1"
$package = (Resolve-Path -LiteralPath $OfficeDirectory).Path
$source = Get-Content "$TabriloRoot/packaging/licenses/LibreOffice-sources.json" -Raw | ConvertFrom-Json
if (!$Installer) { $Installer = "$TabriloRoot/.tools/LibreOffice_26.8.3.msi" }
$msi = Get-Item -LiteralPath $Installer
if ((Get-FileHash -LiteralPath $msi.FullName).Hash -ne $source.msiSha256) { throw 'The MSI differs from the pinned LibreOffice release.' }
$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$database = $windowsInstaller.OpenDatabase($msi.FullName, 0)
function Read-MsiRows($query, $action) {
    $view = $database.OpenView($query)
    try {
        $null = $view.Execute()
        while ($null -ne ($record = $view.Fetch())) { & $action $record }
    } finally { $null = $view.Close() }
}
$directories = @{}
Read-MsiRows 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' {
    param($row)
    $directories[$row.StringData(1)] = @($row.StringData(2), $row.StringData(3).Split(':')[-1].Split('|')[-1])
}
$paths = @{}
foreach ($key in $directories.Keys) {
    $parts = [Collections.Generic.Stack[string]]::new()
    for ($id = $key; $id -and $id -ne 'TARGETDIR'; $id = $directories[$id][0]) {
        if ($directories[$id][1] -ne '.') { $parts.Push($directories[$id][1]) }
    }
    $paths[$key] = $parts.ToArray() -join '/'
}
$componentPaths = @{}
Read-MsiRows 'SELECT `Component`, `Directory_` FROM `Component`' {
    param($row)
    $componentPaths[$row.StringData(1)] = $paths[$row.StringData(2)]
}
$features = @{}
Read-MsiRows 'SELECT `Feature_`, `Component_` FROM `FeatureComponents`' {
    param($row)
    $features[$row.StringData(2)] += @($row.StringData(1))
}
$fileRows = @(Read-MsiRows 'SELECT `Component_`, `FileName`, `FileSize` FROM `File`' {
    param($row)
    $component = $row.StringData(1)
    $relative = ($componentPaths[$component] + '/' + $row.StringData(2).Split('|')[-1]).TrimStart('/')
    $file = [IO.FileInfo]::new((Join-Path $package $relative))
    $size = $row.IntegerData(3)
    [pscustomobject]@{path=$relative;component=$component;features=@($features[$component]);size=$size
        present=$file.Exists;sizeMatches=($file.Exists -and $file.Length -eq $size)}
})
$matched = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($row in $fileRows) {
    if ($row.sizeMatches) { $null = $matched.Add($row.path) }
}
$alternates = @($fileRows | Where-Object { !$_.sizeMatches })
foreach ($row in $alternates) {
    if ($row.component -notlike '*_system_arm64.*' -or !$matched.Contains($row.path)) {
        throw "Missing or different Office component: $($row.component) at $($row.path)"
    }
}
$files = @(Get-ChildItem -LiteralPath $package -Recurse -File)
$extra = @($files | ForEach-Object { [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/') } |
    Where-Object { !$matched.Contains($_) })
if ($extra.Count -ne 1 -or $extra[0] -cne $msi.Name) { throw "Unexpected files outside the MSI component list: $extra" }
$license = Join-Path $package 'LICENSE.html'
$headings = [regex]::Matches((Get-Content -LiteralPath $license -Raw),'(?is)<h2[^>]*>(.*?)</h2>') |
    ForEach-Object { [Net.WebUtility]::HtmlDecode([regex]::Replace($_.Groups[1].Value,'<[^>]*>','')).Trim() }
$output = New-Item -ItemType Directory -Force -Path "$TabriloRoot/artifacts/materials/libreoffice-$($source.version)"
$report = [ordered]@{version=$source.version;upstreamMsi=$source.msiSha256;administrativeMsi=(Get-FileHash (Join-Path $package $msi.Name)).Hash
    files=$fileRows.Count;components=$componentPaths.Count;selectedComponents=@($fileRows | Where-Object sizeMatches | Select-Object -ExpandProperty component -Unique).Count
    destinationFileCount=$files.Count;extraFiles=$extra;matchedPayloadFiles=$matched.Count;alternateArm64Rows=$alternates.Count
    licenseSHA256=(Get-FileHash -LiteralPath $license).Hash;licenseSections=@($headings);componentFiles=$fileRows}
$report | ConvertTo-Json -Depth 6 -Compress | Set-Content -LiteralPath (Join-Path $output.FullName 'windows-components.json')
Write-Output "Matched $($matched.Count) files, $($report.selectedComponents) components and $($alternates.Count) alternate MSI rows. Report: $($output.FullName)/windows-components.json"
