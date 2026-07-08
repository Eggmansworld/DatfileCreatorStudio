# Produces a self-contained, single-file Windows x64 build of Datfile Creator
# Studio in dist\win-x64. The target machine needs no .NET runtime installed.
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$output = Join-Path $PSScriptRoot "dist\$Runtime"
if (Test-Path $output) { Remove-Item -Recurse -Force $output }

dotnet publish (Join-Path $PSScriptRoot "src\DatfileCreatorStudio\DatfileCreatorStudio.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Datfile Creator Studio published to $output"
