<# Exercise live Windows controls and collect native window captures on an interactive desktop. #>
[CmdletBinding()]
param([string] $Image, [switch] $HostedHighContrast)
. "$PSScriptRoot/common.ps1"
if (!$Image) { $Image = Join-Path $TabriloRoot 'tests/fixtures/color.heic' }
$imagePath = (Resolve-Path -LiteralPath $Image).Path
$pdf = Join-Path $TabriloRoot 'tests/fixtures/navigation.pdf'
if ($HostedHighContrast -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) {
    throw 'High-contrast switching is confined to a disposable GitHub-hosted runner.'
}
if ($HostedHighContrast) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DesktopContrast {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct State { public uint Size, Flags; public IntPtr Scheme; }
    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
    public static extern bool Parameters(uint action, uint size, ref State value, uint flags);
}
'@
    $original = [DesktopContrast+State]::new()
    $original.Size = [Runtime.InteropServices.Marshal]::SizeOf($original)
    if (![DesktopContrast]::Parameters(0x42, $original.Size, [ref]$original, 0)) { throw 'Cannot read hosted contrast state.' }
    $contrast = $original
    $contrast.Flags = $contrast.Flags -bor 1
    if (![DesktopContrast]::Parameters(0x43, $contrast.Size, [ref]$contrast, 2)) { throw 'Cannot enable hosted contrast.' }
}
$started = Get-Date
try {
    $repetitions = if ($HostedHighContrast) { 3 } else { 1 }
    for ($index = 0; $index -lt $repetitions; $index++) {
        Invoke-TabriloDotnet @('run','--project','tests/windows/Tabrilo.DesktopTests','-c','Release','--no-build','--',$imagePath,$pdf)
        if ($HostedHighContrast) {
            $report = Get-ChildItem "$TabriloRoot/artifacts/tests/*/desktop.json" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (!$report -or !(Get-Content $report.FullName -Raw | ConvertFrom-Json).highContrast) { throw 'WPF did not observe system high contrast.' }
        }
    }
} catch {
    if ($HostedHighContrast) {
        $events = Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$started; Id=1000,1001,1026} -ErrorAction SilentlyContinue
        $events | Select-Object -First 6 TimeCreated,ProviderName,Message | Format-List | Out-Host
        if (($events.Message -join ' ') -match 'c000000d') {
            & "$PSScriptRoot/inspect-desktop-crash.ps1" -Image $imagePath -Document $pdf
        }
    }
    throw
} finally {
    if ($HostedHighContrast -and ![DesktopContrast]::Parameters(0x43, $original.Size, [ref]$original, 2)) { throw 'Could not restore hosted contrast.' }
}
