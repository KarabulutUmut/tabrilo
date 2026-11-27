function Write-TabriloMaterials([string] $payload, [string] $packages, $lock, [bool] $office) {
    $managed = [ordered]@{}
    $versions = @{}
    foreach ($framework in $lock.dependencies.Values) {
        foreach ($pair in $framework.GetEnumerator()) {
            if ($pair.Value.resolved) { $versions[$pair.Key.ToLowerInvariant()] = $pair.Value.resolved }
        }
    }
    foreach ($id in 'microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64') {
        $versions[$id] = '10.0.15'
    }
    foreach ($id in ($versions.Keys | Sort-Object)) {
        $version = $versions[$id]
        $root = Join-Path $packages "$id/$version"
        [xml]$spec = Get-Content -LiteralPath "$root/$id.nuspec" -Raw
        $meta = $spec.package.metadata
        $managed[$id] = [ordered]@{ version = $version; origin = "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg"
            sha512 = (Get-Content -LiteralPath "$root/$id.$version.nupkg.sha512" -Raw).Trim()
            repository = [string]$meta.repository.url; revision = [string]$meta.repository.commit
            license = [string]$meta.license.InnerText; licenseUrl = [string]$meta.licenseUrl }
    }
    $magick = @(foreach ($line in (Get-Content "$packages/magick.net-q8-x64/15.1.0/Notice.txt")) {
        if ($line -match '^\[\s*(\S+) (\S+) \([\d-]+\) \((https://[^)]+)\)\s*\]') {
            [ordered]@{ name = $Matches[1]; version = $Matches[2]; source = $Matches[3] }
        }
    })
    if ($magick.Count -lt 1) { throw 'Native codec inventory is missing.' }
    $native = @(foreach ($file in (@(Get-ChildItem -File "$payload/tools/qpdf") + @(Get-Item "$payload/Magick.Native-Q8-x64.dll"))) {
        [ordered]@{ file = [IO.Path]::GetRelativePath($payload, $file.FullName); version = $file.VersionInfo.FileVersion
            sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash }
    })
    if ($office) {
        $sources = Get-Content "$TabriloRoot/packaging/licenses/LibreOffice-sources.json" -Raw | ConvertFrom-Json
        if (!(Select-String -LiteralPath "$payload/tools/office/program/version.ini" -SimpleMatch "buildid=$($sources.sourceCommit)")) {
            throw 'Office build and recorded corresponding source differ.'
        }
    }
    Copy-Item -LiteralPath "$TabriloRoot/packaging/materials.json","$TabriloRoot/shared/dependencies.json" -Destination "$payload/licenses"
    [ordered]@{ version = 1; platform = 'windows-x64'; nuget = $managed; native = $native; magickDelegates = $magick } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$payload/licenses/components.json"
}
