<# Runs the NSIS installer against small synthetic core and Office payloads under a fresh per-user identity. #>
. "$PSScriptRoot/common.ps1"
. "$PSScriptRoot/compile-installer.ps1"
$kind = 'Tabrilo-Receipt-Test-' + [guid]::NewGuid().ToString('N')
$work = Join-Path $TabriloRoot "artifacts/tests/$kind"
$install = Join-Path $env:LOCALAPPDATA "Programs/$kind"
function BuildFixture($label, $version, [bool] $office) {
    $output = Join-Path $work $label
    $payload = Join-Path $output 'payload'
    $null = New-Item -ItemType Directory -Path "$payload/licenses"
    [IO.File]::WriteAllText("$payload/Tabrilo.exe", $label)
    if ($office) {
        $null = New-Item -ItemType Directory -Path "$payload/tools/office"
        [IO.File]::WriteAllText("$payload/tools/office/sample", 'optional')
    }
    New-TabriloInstaller $payload $output $version $kind $office | Out-Host
    return (Get-ChildItem -LiteralPath $output -Filter '*Setup.exe').FullName
}
function RunSetup($exe, $expected = 0) {
    $arguments = '/S'
    if ([IO.Path]::GetFileName($exe) -eq 'Uninstall.exe') {
        $arguments += ' _?=' + (Split-Path $exe)
        $copy = Join-Path $work 'remove.exe'
        Copy-Item -LiteralPath $exe -Destination $copy -Force
        $exe = $copy
    }
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($expected -ne $process.ExitCode) { throw "Expected $expected from $exe, got $($process.ExitCode)." }
}
function Require($value, $message) { if (!$value) { throw $message } }

$core = BuildFixture 'core' '1.0.0' $false
$changed = BuildFixture 'changed' '1.0.0' $false
$office = BuildFixture 'office' '1.0.0' $true
$upgrade = BuildFixture 'next' '1.0.1' $false
$v1 = Join-Path $install 'versions/1.0.0'
$v2 = Join-Path $install 'versions/1.0.1'
RunSetup $core
RunSetup $office 2
RunSetup $changed 2
Require ((Get-Content "$v1/Tabrilo.exe") -eq 'core' -and !(Test-Path "$v1/tools/office")) 'Unexpected core component change.'
RunSetup $core
Remove-Item -LiteralPath "$v1/licenses/NSIS-COPYING.txt"
$lock = [IO.File]::Open("$v1/Tabrilo.exe", 'Open', 'Read', 'None')
try { RunSetup $core 1 } finally { $lock.Dispose() }
Require (!(Test-Path "$v1/licenses/NSIS-COPYING.txt")) 'Failed repair altered installed entries.'
RunSetup $core
Require (Test-Path "$v1/licenses/NSIS-COPYING.txt") 'Missing file was not repaired.'
RunSetup "$v1/Uninstall.exe"
RunSetup $office
RunSetup $core 2
Require ((Get-Content "$v1/tools/office/sample") -eq 'optional') 'Office was lost on same-version rejection.'
[IO.File]::WriteAllText("$v1/keep.txt", 'unrelated')
RunSetup $upgrade
Require ((Get-Content "$v2/Tabrilo.exe") -eq 'next' -and !(Test-Path "$v2/tools/office")) 'New core version is incomplete.'
RunSetup "$v2/Uninstall.exe"
Require (!(Test-Path "$v1/tools/office/sample") -and !(Test-Path "$v1/Tabrilo.exe") -and !(Test-Path $v2) -and
    (Get-Content "$v1/keep.txt") -eq 'unrelated') 'Uninstall did not respect version ownership.'
# Remove just our synthetic marker and its now-empty parents.
Remove-Item -LiteralPath "$v1/keep.txt"
foreach ($dir in $v1, (Split-Path $v1), $install) { [IO.Directory]::Delete($dir) }
Write-Output "PASS component transitions, changed bits, identical reinstall, blocked and successful repair, upgrade and uninstall. $work"
