<# Rebuild the matched Windows Q8 native DLL using a rebuilt libde265 on a VS 2026 C++ x64 machine. #>
[CmdletBinding()]
param()
. "$PSScriptRoot/common.ps1"
$info = Get-Content "$TabriloRoot/packaging/licenses/Magick-Native-sources.json" -Raw | ConvertFrom-Json
$cache = (New-Item -ItemType Directory -Force -Path "$TabriloRoot/artifacts/materials/magick-native").FullName
$work = (New-Item -ItemType Directory -Path "$cache/relink-$([guid]::NewGuid().ToString('N').Substring(0,8))").FullName
function Checkout($url, $revision, $folder) {
    & git init -q $folder
    if ($LASTEXITCODE) { throw 'Could not create source checkout.' }
    & git -C $folder fetch -q --depth=1 $url $revision
    if ($LASTEXITCODE) { throw "Source fetch failed for $url." }
    & git -C $folder checkout -q --detach FETCH_HEAD
    if ($LASTEXITCODE -or (git -C $folder rev-parse HEAD) -cne $revision) { throw 'Unexpected source revision.' }
}
foreach ($asset in $info.inputs) {
    $file = Join-Path $cache $asset.name
    if (!(Test-Path -LiteralPath $file)) {
        & curl.exe --fail --silent --show-error --location --retry 2 --proto '=https' --proto-redir '=https' --output $file $asset.url
        if ($LASTEXITCODE) { throw "Download failed for $($asset.name)." }
    }
    if ((Get-FileHash -LiteralPath $file).Hash -ne $asset.sha256) { throw "Invalid material: $file" }
}
$native = Join-Path $work 'native'
Checkout 'https://github.com/dlemstra/Magick.Native.git' $info.commit $native
$engine = Join-Path $native 'src/ImageMagick'
Checkout 'https://github.com/ImageMagick/ImageMagick.git' $info.imageMagickCommit "$engine/ImageMagick"
$de265 = $info.delegateSources | Where-Object name -ceq 'de265'
Checkout $de265.url $de265.revision "$work/de265"
$null = New-Item -ItemType Directory -Force -Path "$engine/Configure"
Copy-Item -LiteralPath "$cache/Configure.Release.x64.exe" -Destination "$engine/Configure"
[IO.Compression.ZipFile]::ExtractToDirectory("$cache/files.zip", "$engine/Configure")
[IO.Compression.ZipFile]::ExtractToDirectory("$cache/windows-x64-static-noOpenMP-linked-runtime.zip", "$engine/Artifacts")
# A version marker demonstrates that our replacement object actually enters the final linked DLL.
$source = "$work/de265/libde265/de265.cc"
$code = [IO.File]::ReadAllText($source)
$version = 'return (LIBDE265_VERSION);'
if (!$code.Contains($version)) { throw 'Unexpected de265 version function.' }
[IO.File]::WriteAllText($source, $code.Replace($version, 'return LIBDE265_VERSION "-tabrilo-relink-check";'))
& cmake -S "$work/de265" -B "$work/de265-build" -G 'Visual Studio 18 2026' -A x64 -DBUILD_SHARED_LIBS=OFF -DENABLE_SDL=OFF -DENABLE_ENCODER=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
if ($LASTEXITCODE) { throw 'Could not configure replacement libde265.' }
& cmake --build "$work/de265-build" --config Release --target de265 --parallel 4
if ($LASTEXITCODE) { throw 'Could not compile replacement libde265.' }
$replacement = @(Get-ChildItem -LiteralPath "$work/de265-build" -Recurse -File | Where-Object Name -in 'de265.lib','libde265.lib')
if ($replacement.Count -ne 1) { throw 'Expected one replacement static library.' }
Copy-Item -LiteralPath $replacement[0].FullName -Destination "$engine/Artifacts/lib/CORE_RL_de265_.lib" -Force
Push-Location "$native/build/windows"
try {
    & ./build.cmd Release Q8 x64 noHdri noOpenMP
    if ($LASTEXITCODE) { throw 'The vendor Q8 build failed.' }
} finally { Pop-Location }
$dll = Get-Item -LiteralPath "$native/src/Magick.Native/bin/ReleaseQ8/x64/Magick.Native-Q8-x64.dll"
if (![Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($dll.FullName)).Contains('-tabrilo-relink-check')) { throw 'Linked replacement marker missing.' }
$driver = Join-Path $work 'driver'
Invoke-TabriloDotnet @('restore',$TabriloProjects.Solution,'--locked-mode')
Invoke-TabriloDotnet @('build',$TabriloProjects.Tests,'-c','Release','--no-restore','-o',$driver)
$target = @(Get-ChildItem -LiteralPath $driver -Recurse -Filter 'Magick.Native-Q8-x64.dll' -File)
if ($target.Count -ne 1) { throw 'Expected one native DLL in the standalone test driver.' }
Copy-Item -LiteralPath $dll.FullName -Destination $target[0].FullName -Force
Invoke-TabriloDotnet @("$driver/Tabrilo.Tests.dll",'--relinked-heic',"$TabriloRoot/tests/fixtures/color.heic")
[ordered]@{nativeSource=$info.commit;replacementSource=$de265.revision;sha256=(Get-FileHash $dll.FullName).Hash;
    replacementMarker=$true;heicDecode=$true} | ConvertTo-Json | Set-Content -LiteralPath "$work/verification.json"
Write-Output "Replacement and HEIC decode verified at $work."
