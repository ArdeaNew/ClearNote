#Requires -Version 5.1
<#
.SYNOPSIS
Creates a verified, portable ClearNote package containing the app, README and license.
.EXAMPLE
.\tools\Package.ps1 -OutputDirectory D:\Releases -Version 1.9.4
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '..'))
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$metadata = Get-Content -LiteralPath (Join-Path $sourceRoot 'Folio.App\Folio.App.csproj') -Raw
    $Version = [string]$metadata.Project.PropertyGroup.Version
    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$') {
        throw 'The application project must declare a valid package version.'
    }
}
$workRoot = Join-Path $sourceRoot 'work'
$stagingRoot = Join-Path $workRoot ('package-' + [Guid]::NewGuid().ToString('N'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'packages'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$archivePath = Join-Path $OutputDirectory "ClearNote-$Version-Windows.zip"

if (Test-Path -LiteralPath $archivePath) {
    throw "The package already exists. Choose another output directory or version: $archivePath"
}

$inputs = @(
    [pscustomobject]@{ Source = Join-Path $projectRoot 'app\ClearNote.exe'; Name = 'ClearNote.exe' }
    [pscustomobject]@{ Source = Join-Path $projectRoot 'docs\PORTABLE.md'; Name = 'README.md' }
    [pscustomobject]@{ Source = Join-Path $projectRoot 'LICENSE'; Name = 'LICENSE' }
)
foreach ($inputFile in $inputs) {
    if (-not (Test-Path -LiteralPath $inputFile.Source -PathType Leaf)) {
        throw "A required package file is missing: $($inputFile.Source)"
    }
    $inputFile | Add-Member -NotePropertyName SHA256 -NotePropertyValue (
        Get-FileHash -LiteralPath $inputFile.Source -Algorithm SHA256
    ).Hash
}
$executable = Get-Item -LiteralPath $inputs[0].Source
$numericVersion = ($Version -split '[-+]')[0]
if ($executable.VersionInfo.ProductName -ne 'ClearNote' -or
    ([version]$executable.VersionInfo.FileVersion).ToString(3) -ne $numericVersion) {
    throw 'The executable product and version must match the requested package.'
}

function Remove-PackageStaging {
    param([string]$Path, [string]$WorkDirectory)

    $absoluteWork = [IO.Path]::GetFullPath($WorkDirectory).TrimEnd('\', '/')
    $absolutePath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $insideWork = $absolutePath.StartsWith(
        $absoluteWork + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )
    if (-not $insideWork -or
        -not [string]::Equals([IO.Path]::GetDirectoryName($absolutePath), $absoluteWork,
            [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($absolutePath) -notmatch '^package-[0-9a-f]{32}$') {
        throw "Refusing to remove staging outside src/work: $absolutePath"
    }
    if (-not (Test-Path -LiteralPath $absolutePath)) { return }

    # Never follow a link while cleaning a directory created by this script.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($absolutePath)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in @(Get-Item -LiteralPath $directory -Force) +
            @(Get-ChildItem -LiteralPath $directory -Force)) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Staging contains a link and has been preserved: $($entry.FullName)"
            }
            if ($entry.PSIsContainer -and $entry.FullName -ne $directory) {
                $pending.Push($entry.FullName)
            }
        }
    }
    Remove-Item -LiteralPath $absolutePath -Recurse -Force
}

New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
if (((Get-Item -LiteralPath $workRoot -Force).Attributes -band
    [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The staging work directory must not be a link: $workRoot"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

try {
    $payloadRoot = Join-Path $stagingRoot 'payload'
    $folioRoot = Join-Path $payloadRoot 'ClearNote'
    New-Item -ItemType Directory -Path $folioRoot | Out-Null
    foreach ($inputFile in $inputs) {
        Copy-Item -LiteralPath $inputFile.Source -Destination (Join-Path $folioRoot $inputFile.Name)
    }

    $temporaryArchive = Join-Path $stagingRoot 'package.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $payloadRoot, $temporaryArchive, [IO.Compression.CompressionLevel]::Optimal, $false
    )
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryArchive)
    try {
        if ($archive.Entries.Count -ne 3) {
            throw 'The package must contain exactly three files.'
        }
        foreach ($inputFile in $inputs) {
            $entry = $archive.GetEntry('ClearNote/' + $inputFile.Name)
            if ($null -eq $entry) { throw "A package entry is missing: $($inputFile.Name)" }
            $stream = $entry.Open()
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try {
                $entryHash = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
            }
            finally {
                $sha256.Dispose()
                $stream.Dispose()
            }
            $currentHash = (Get-FileHash -LiteralPath $inputFile.Source -Algorithm SHA256).Hash
            if ($entryHash -ne $inputFile.SHA256 -or $currentHash -ne $inputFile.SHA256) {
                throw "Package verification failed or the source changed: $($inputFile.Name)"
            }
        }
    }
    finally { $archive.Dispose() }

    # File.Move refuses an existing destination, including one created during packaging.
    [IO.File]::Move($temporaryArchive, $archivePath)
    $result = [pscustomobject]@{
        Archive = $archivePath
        Version = $Version
        Files = 3
        SHA256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    }
}
finally { Remove-PackageStaging -Path $stagingRoot -WorkDirectory $workRoot }

$result


