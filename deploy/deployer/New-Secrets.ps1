<#
.SYNOPSIS
  Creates the two key files the API needs outside development: openiddict-signing.pfx and openiddict-encryption.pfx.

.DESCRIPTION
  Self-signed, 5 years, generated with .NET only (nothing is added to the Windows certificate store). Without -Apply it
  only says what it would create. It never overwrites an existing file: losing a key signs everyone out.
  Keep the folder readable only by your account (Properties > Security) and covered by BitLocker.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Path = 'C:\CollectionMaxxing\secrets',
    [string]$Password = '',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$doApply = $Apply.IsPresent -and -not $WhatIfPreference
$files = @(
    @{ Name = 'openiddict-signing.pfx'; Subject = 'CollectionMaxxing token signing' },
    @{ Name = 'openiddict-encryption.pfx'; Subject = 'CollectionMaxxing token encryption' }
)
foreach ($f in $files) {
    $target = Join-Path $Path $f.Name
    if (Test-Path -LiteralPath $target) { Write-Host "exists, leaving it alone: $target"; continue }
    if (-not $doApply) { Write-Host "dry run, would create: $target"; continue }
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=$($f.Subject)", $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(5))
    $bytes = if ($Password) { $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $Password) }
    else { $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx) }
    [System.IO.File]::WriteAllBytes($target, $bytes)
    Write-Host "created: $target"
}
