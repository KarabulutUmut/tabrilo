$ErrorActionPreference = 'Stop'
$TabriloRoot = Split-Path $PSScriptRoot -Parent
$TabriloProjects = @{
    Solution = Join-Path $TabriloRoot 'Tabrilo.slnx'
    App = Join-Path $TabriloRoot 'apps/windows/Tabrilo.Windows/Tabrilo.Windows.csproj'
    Tests = Join-Path $TabriloRoot 'tests/windows/Tabrilo.Tests/Tabrilo.Tests.csproj'
}
$TabriloSdk = (Get-Content (Join-Path $TabriloRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version

function Get-TabriloDotnet {
    $local = Join-Path $TabriloRoot '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (!$dotnet) { throw "Install the native Windows x64 .NET SDK $TabriloSdk." }
    return $dotnet.Source
}

function Invoke-TabriloDotnet([string[]] $Arguments) {
    Push-Location $TabriloRoot
    try {
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_NOLOGO = '1'
        $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
        $env:DOTNET_CLI_HOME = Join-Path $TabriloRoot '.tools'
        $env:NUGET_PACKAGES = Join-Path $TabriloRoot '.nuget/packages'
        & (Get-TabriloDotnet) @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) exited $LASTEXITCODE." }
    } finally { Pop-Location }
}
