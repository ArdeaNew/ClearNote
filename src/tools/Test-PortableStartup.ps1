#Requires -Version 7.2
<#
.SYNOPSIS
Starts an isolated copy of the published EXE without showing a window or using an installed runtime.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Path).ProviderPath
$product = (Get-Item -LiteralPath $source).VersionInfo
if ($product.ProductName -ne 'ClearNote' -or [version]$product.FileVersion -lt [version]'1.9.5.0') {
    throw 'This executable does not support the noninteractive startup check.'
}
& (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Path $source | Out-Null
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$root = Join-Path $temporaryRoot ('ClearNote-portable-' + [Guid]::NewGuid().ToString('N'))
$run = Join-Path $root 'run'
$extract = Join-Path $root 'extracted'
$emptyRuntime = Join-Path $root 'no-installed-runtime'
$data = Join-Path $root 'Log\Data'
$report = Join-Path $root 'startup.json'
foreach ($directory in @($run, $extract, $emptyRuntime)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$executable = Join-Path $run 'ClearNote.exe'
Copy-Item -LiteralPath $source -Destination $executable
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $hash) { throw 'The isolated executable differs from the release input.' }

$start = [Diagnostics.ProcessStartInfo]::new($executable)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WorkingDirectory = $run
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @('--verify-portable', $report, '--data', $data)) { $start.ArgumentList.Add($argument) }
$start.Environment['DOTNET_ROOT'] = $emptyRuntime
$start.Environment['DOTNET_ROOT_X64'] = $emptyRuntime
$start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $extract
$start.Environment['DOTNET_HOST_TRACE'] = '1'
$start.Environment['DOTNET_HOST_TRACEFILE'] = Join-Path $root 'host-trace.txt'
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
try {
    if (-not $process.Start()) { throw 'The standalone executable did not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "Standalone startup exceeded 30 seconds. Evidence: $root"
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report -PathType Leaf)) {
        throw "Standalone startup failed ($($process.ExitCode)). $($stderr.GetAwaiter().GetResult()) Evidence: $root"
    }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    # .NET 10 may statically link CoreCLR into the verified apphost. Older hosts extract it.
    $externalCoreClr = @($result.CoreClrPaths | Where-Object {
        -not ([IO.Path]::GetFullPath($_)).StartsWith($extract + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    })
    if (-not $result.Success -or -not $result.WindowCreated -or $result.WindowVisible -or
        $result.WpfAssembly -ne 'PresentationFramework' -or $result.Runtime -notlike '.NET 10.*' -or
        $externalCoreClr.Count -gt 0 -or $result.CoreLibLocation -ne '' -or $result.WpfLocation -ne '' -or
        -not [string]::Equals([IO.Path]::GetFullPath($result.LibraryPath), $data, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($result.BaseDirectory.TrimEnd('\'), $run, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($result.ProcessPath, $executable, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The EXE did not initialize WPF using only its bundled runtime and isolated library. Evidence: $root"
    }
    [pscustomobject]@{
        StandaloneStartup = 'Passed'
        BundledRuntime = $result.Runtime
        WindowShown = $result.WindowVisible
        SHA256 = $hash
        Evidence = $root
    }
}
finally { $process.Dispose() }
