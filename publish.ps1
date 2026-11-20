<#
.SYNOPSIS
  Builds a single-file AllianceStatus.exe into .\dist

.EXAMPLE
  .\publish.ps1                      # both variants for x64
  .\publish.ps1 -Mode standalone     # no .NET install needed on the target PC
  .\publish.ps1 -Runtime win-arm64
#>
param(
    [ValidateSet('framework', 'standalone', 'both')] [string] $Mode = 'both',
    [ValidateSet('win-x64', 'win-arm64')] [string] $Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\AllianceStatus\AllianceStatus.csproj'
$dist = Join-Path $root 'dist'

function Publish([string] $name, [string[]] $extra) {
    $out = Join-Path $dist "$Runtime-$name"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish $project -c Release -r $Runtime -p:PublishSingleFile=true -o $out @extra
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($name)" }
    $zip = Join-Path $dist "AllianceStatus-$Runtime-$name.zip"
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
    Write-Host "-> $out\AllianceStatus.exe" -ForegroundColor Green
}

if ($Mode -in 'framework', 'both') {
    # Small exe; needs the .NET 10 Desktop Runtime on the target machine.
    Publish 'framework' @('--self-contained', 'false')
}
if ($Mode -in 'standalone', 'both') {
    # Larger exe; runs on any Windows 10 1809+/11 PC without installing .NET.
    Publish 'standalone' @('--self-contained', 'true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true')
}
