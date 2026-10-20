#Requires -Version 7.0
<#
.SYNOPSIS
Verifies the exact ClearNote signing result before packaging.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SignedDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedSignerCertificateSha256,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$ExpectedFileVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Authenticode verification requires Windows.' }

$directory = Get-Item -LiteralPath $SignedDirectory -Force
if (-not $directory.PSIsContainer -or
    ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The signing result must be an ordinary directory.'
}
$entries = @(Get-ChildItem -LiteralPath $directory.FullName -Force)
if ($entries.Count -ne 1 -or $entries[0].Name -cne 'ClearNote.exe' -or
    $entries[0].PSIsContainer -or
    ($entries[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The signing result must contain only ClearNote.exe, without directories or links.'
}
$exe = $entries[0]
if ($exe.VersionInfo.ProductName -ne 'ClearNote' -or
    $exe.VersionInfo.FileVersion -ne $ExpectedFileVersion) {
    throw 'The signing result does not match the ClearNote release metadata.'
}

function Assert-EnhancedKeyUsage {
    param(
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$RequiredOid,
        [string]$Description
    )
    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -eq '2.5.29.37') {
            $usage = [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
                $extension, $extension.Critical)
            if (@($usage.EnhancedKeyUsages | Where-Object Value -eq $RequiredOid).Count -gt 0) {
                return
            }
        }
    }
    throw "The $Description certificate lacks the required extended key usage."
}

$signature = Get-AuthenticodeSignature -LiteralPath $exe.FullName
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
    $null -eq $signature.TimeStamperCertificate -or $signature.SignatureType -ne 'Authenticode') {
    throw 'The executable must have a valid embedded Authenticode signature and timestamp.'
}
$certificateSha256 = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData($signature.SignerCertificate.RawData))
if ($certificateSha256 -ne $ExpectedSignerCertificateSha256) {
    throw 'The signature does not use the approved production certificate.'
}
Assert-EnhancedKeyUsage $signature.SignerCertificate '1.3.6.1.5.5.7.3.3' 'publisher'
Assert-EnhancedKeyUsage $signature.TimeStamperCertificate '1.3.6.1.5.5.7.3.8' 'timestamp'

$sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$signTool = Get-ChildItem -LiteralPath $sdkBin -Directory |
    Where-Object Name -Match '^10\.\d+\.\d+\.\d+$' |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($signTool)) {
    throw 'Microsoft SignTool from the Windows SDK is required for trust verification.'
}
& $signTool verify /pa /all /tw /v $exe.FullName
if ($LASTEXITCODE -ne 0) {
    throw 'Windows signature or timestamp trust verification failed, including any warnings.'
}

[pscustomobject]@{
    File = $exe.Name
    FileVersion = $ExpectedFileVersion
    CertificateSHA256 = $certificateSha256
    SHA256 = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash
    Verified = $true
}
