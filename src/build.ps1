param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$checkRoot = Join-Path $PSScriptRoot 'work\checks'
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\app'))

dotnet build (Join-Path $PSScriptRoot 'Folio.slnx') -c $Configuration -m:1 -p:UseSharedCompilation=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet run --project (Join-Path $PSScriptRoot 'Folio.Tests\Folio.Tests.csproj') -c $Configuration --no-build -- $checkRoot
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
dotnet run --project (Join-Path $PSScriptRoot 'Folio.Tests\Folio.UiTests\Folio.UiTests.csproj') -c $Configuration --no-build -- $checkRoot
if ($LASTEXITCODE -ne 0) { throw 'UI component checks failed.' }
dotnet publish (Join-Path $PSScriptRoot 'Folio.App\Folio.App.csproj') -c $Configuration -p:PublishProfile=Portable -p:DebugType=None -p:DebugSymbols=false -m:1 -p:UseSharedCompilation=false --nologo -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
& (Join-Path $PSScriptRoot 'tools\Verify-Portable.ps1') -Path (Join-Path $publishRoot 'ClearNote.exe')
& (Join-Path $PSScriptRoot 'tools\Test-PortableStartup.ps1') -Path (Join-Path $publishRoot 'ClearNote.exe')
Write-Output "Published to $publishRoot"
