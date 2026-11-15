<#
.SYNOPSIS
Checks the six root document budgets against the table in AGENTS.md.
.PARAMETER SelfTest
Also exercise warning, exact-cap, over-budget and missing-file cases.
#>
[CmdletBinding()]
param([switch] $SelfTest, [switch] $Help)

$ErrorActionPreference = 'Stop'
if ($Help) { Get-Help $PSCommandPath; exit 0 }

function Get-Budgets([string] $Root) {
    $agents = Join-Path $Root 'AGENTS.md'
    $table = [regex]::Match([IO.File]::ReadAllText($agents),
        '(?m)^\| File \| Maximum lines \|\r?\n\| --- \| ---: \|\r?\n(?<rows>(?:\| [A-Z_]+\.md \| [0-9]+ \|\r?\n?)+)')
    if (!$table.Success) { throw 'The documentation budget table is missing from AGENTS.md.' }
    $budgets = @{}
    foreach ($row in [regex]::Matches($table.Groups['rows'].Value, '\| ([A-Z_]+\.md) \| ([0-9]+) \|')) {
        $name = $row.Groups[1].Value
        if ($budgets.ContainsKey($name)) { throw "Duplicate budget for $name." }
        $budgets.Add($name, [int] $row.Groups[2].Value)
    }
    $required = 'AGENTS.md', 'README.md', 'PRODUCT.md', 'ARCHITECTURE.md', 'UI_SYSTEM.md', 'STATUS.md'
    if (Compare-Object ($required | Sort-Object) ($budgets.Keys | Sort-Object)) {
        throw 'The AGENTS.md table must name each of the six root documents exactly once.'
    }
    return $budgets
}

function Measure-Document([string] $Path, [int] $Limit) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return @{ State = 'missing'; Count = 0 } }
    $count = [IO.File]::ReadAllLines($Path).Length
    $state = if ($count -gt $Limit) { 'over budget' }
        elseif ($count * 100 -ge $Limit * 80) { 'near budget' }
        else { 'ok' }
    return @{ State = $state; Count = $count }
}

try {
    if ($SelfTest) {
        $fixture = [IO.Path]::GetTempFileName()
        try {
            foreach ($case in @(@(7, 'ok'), @(8, 'near budget'), @(10, 'near budget'), @(11, 'over budget'))) {
                [IO.File]::WriteAllLines($fixture, [string[]] (@('') * $case[0]))
                if ((Measure-Document $fixture 10).State -ne $case[1]) { throw "Budget self-test failed for $($case[0]) lines." }
            }
            [IO.File]::Delete($fixture)
            if ((Measure-Document $fixture 10).State -ne 'missing') { throw 'Missing-file self-test failed.' }
        } finally { [IO.File]::Delete($fixture) }
        Write-Output 'Documentation checker self-test passed.'
    }

    $root = Split-Path $PSScriptRoot -Parent
    $budgets = Get-Budgets $root
    $failed = $false
    foreach ($name in ($budgets.Keys | Sort-Object)) {
        $limit = $budgets[$name]
        $result = Measure-Document (Join-Path $root $name) $limit
        $message = "$name $($result.Count)/$limit ($($result.State))"
        if ($result.State -eq 'near budget') { Write-Warning $message }
        else { Write-Output $message }
        if ($result.State -in 'missing', 'over budget') { $failed = $true }
    }
    if ($failed) { exit 1 }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
