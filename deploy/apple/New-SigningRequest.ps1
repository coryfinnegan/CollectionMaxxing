<#
.SYNOPSIS
  Creates the private key and the certificate signing request (CSR) for the Apple Distribution certificate (ADR-024).

.DESCRIPTION
  Writes <Root>\secrets\apple\distribution.key.pem and distribution.csr, with .NET only (no openssl, no Mac). Upload the
  .csr at developer.apple.com > Certificates > + > Apple Distribution, then run Publish-SigningSecrets.ps1 with the .cer
  Apple gives you. Without -Apply it only says what it would create. It never overwrites the key: the certificate only
  works with the key it was made from. For the yearly renewal, pass -Renew to set the old key aside and make a new pair.
  Keep the folder readable only by your account and covered by BitLocker, like the rest of the secrets folder.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Root = 'C:\CollectionMaxxing',
    [switch]$Renew,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../deployer/Cm.Common.ps1')
. (Join-Path $PSScriptRoot 'Cm.Apple.ps1')

$doApply = $Apply.IsPresent -and -not $WhatIfPreference
$dir = Join-Path $Root 'secrets/apple'
$key = Join-Path $dir $script:CmAppleKeyFile
$csr = Join-Path $dir $script:CmAppleRequestFile

if ((Test-Path -LiteralPath $key) -and -not $Renew) {
    Write-Host "exists, leaving it alone: $key"
    Write-Host "the request to upload is: $csr (pass -Renew to make a new key, for example for the yearly renewal)"
    exit 0
}
if (-not $doApply) {
    if (Test-Path -LiteralPath $key) { Write-Host "dry run, would move the current key aside to distribution.key.<date>.pem" }
    Write-Host "dry run, would create: $key"
    Write-Host "dry run, would create: $csr"
    exit 0
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
if (Test-Path -LiteralPath $key) {
    # Kept, not deleted: the certificate made from it keeps signing until it expires or the new one is published.
    $aside = Join-Path $dir ("distribution.key.{0:yyyyMMdd-HHmmss}.pem" -f [DateTime]::UtcNow)
    Move-Item -LiteralPath $key -Destination $aside
    Write-Host "moved the old key aside: $aside"
}
$pair = New-CmSigningRequest
Set-Content -LiteralPath $key -Value $pair.KeyPem -Encoding ascii -NoNewline
Set-Content -LiteralPath $csr -Value $pair.RequestPem -Encoding ascii -NoNewline
Write-Host "created: $key"
Write-Host "created: $csr"
Write-Host 'next: upload the .csr at developer.apple.com > Certificates > + > Apple Distribution, and download the .cer'
