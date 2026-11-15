<#
.SYNOPSIS
Validates shared manifests, recorded fixture digests and GitHub Actions workflows.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $manifests = @(Get-ChildItem shared -Filter '*.json' -File -Recurse)
    $manifests += Get-Item global.json, tests/fixtures/source.json
    foreach ($manifest in $manifests) {
        if (!(Test-Json -LiteralPath $manifest.FullName)) {
            throw "Invalid JSON in $($manifest.Name)."
        }
    }
    if (!(Test-Json -LiteralPath 'shared/contracts/commands.json' -SchemaFile 'shared/contracts/commands.schema.json')) {
        throw 'The command catalog does not match its schema.'
    }

    $dependencies = Get-Content shared/dependencies.json -Raw | ConvertFrom-Json
    $sdk = (Get-Content global.json -Raw | ConvertFrom-Json).sdk
    if ($sdk.version -ne $dependencies.assets.sdk.version -or $sdk.rollForward -ne 'disable') {
        throw 'Pin the same exact SDK in global.json and shared/dependencies.json.'
    }

    $origins = Get-Content tests/fixtures/source.json -Raw | ConvertFrom-Json -AsHashtable
    foreach ($name in 'color.heic', 'entry.rar') {
        if ((Get-FileHash -LiteralPath "tests/fixtures/$name").Hash -ne $origins[$name].sha256) {
            throw "Fixture checksum does not match for $name."
        }
    }

    $workflows = @(Get-ChildItem .github/workflows -File | Where-Object Extension -in '.yml', '.yaml')
    $linter = Join-Path $root '.tools/actionlint/actionlint.exe'
    if (!$workflows -or !(Test-Path -LiteralPath $linter -PathType Leaf)) {
        throw 'Workflow files and the pinned linter are required. Run scripts/restore-tools.ps1 -Validation.'
    }
    foreach ($workflow in $workflows) {
        foreach ($reference in (Select-String -LiteralPath $workflow.FullName -Pattern '^\s*(?:-\s*)?uses:\s*(\S+)')) {
            if ($reference.Matches[0].Groups[1].Value -cnotmatch '^(?:\./|[^@#]+@[0-9a-f]{40}$)') {
                throw "Pin the action at $($workflow.Name) line $($reference.LineNumber) to a full commit SHA."
            }
        }
    }

    & $linter -color @($workflows.FullName)
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Output 'Shared manifests and workflows validated.'
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} finally {
    Pop-Location
}
