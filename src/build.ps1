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
Write-Output "Published to $publishRoot"
