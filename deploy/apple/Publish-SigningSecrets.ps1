<#
.SYNOPSIS
  Stores the TestFlight signing pieces in the repo's `testflight` GitHub environment (ADR-024).

.DESCRIPTION
  Reads the key New-SigningRequest.ps1 made, plus the files you download from Apple: the Apple Distribution certificate
  (.cer), the App Store Connect provisioning profile (.mobileprovision) and, the first time, the App Store Connect API key
  (.p8 and its issuer ID; the key ID is read from the file name). It checks they fit together and that the profile is
  for this app, then uses gh (signed in as the repo owner) to create the environment, limit it to main and v* tags, and
  set its secrets and variables. Values reach gh on standard input and never appear on a command line or in the output.

  Without -Apply it checks everything and prints what it would store. Safe to run again: it only overwrites what it sets.
  For the yearly renewal, leave out the API key parameters; the key already stored keeps working.

.EXAMPLE
  ./Publish-SigningSecrets.ps1 -Certificate ~\Downloads\distribution.cer -ProvisioningProfile ~\Downloads\CollectionMaxxing_App_Store.mobileprovision -ApiKey ~\Downloads\AuthKey_ABCDE12345.p8 -IssuerId <issuer id> -Apply
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$Certificate,
    [Parameter(Mandatory)][string]$ProvisioningProfile,
    [string]$ApiKey = '',
    [string]$KeyId = '',
    [string]$IssuerId = '',
    [string]$Root = 'C:\CollectionMaxxing',
    [string]$Repo = 'coryfinnegan/CollectionMaxxing',
    [string]$BundleId = 'io.github.coryfinnegan.collectionmaxxing',
    [string]$Environment = 'testflight',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../deployer/Cm.Common.ps1')
. (Join-Path $PSScriptRoot 'Cm.Apple.ps1')

$doApply = $Apply.IsPresent -and -not $WhatIfPreference
Initialize-Cm -Apply $doApply
Use-CmPortableTools -Root $Root

$keyFile = Join-Path $Root "secrets/apple/$($script:CmAppleKeyFile)"
if (-not (Test-Path -LiteralPath $keyFile)) {
    Write-CmLog "no key at ${keyFile}: run New-SigningRequest.ps1 -Apply first, and make the certificate from its .csr" 'ERROR'
    exit 2
}
foreach ($file in @($Certificate, $ProvisioningProfile, $ApiKey) | Where-Object { $_ }) {
    if (-not (Test-Path -LiteralPath $file)) { Write-CmLog "file not found: $file" 'ERROR'; exit 2 }
}
# The key ID is in the name Apple gives the file (AuthKey_<key ID>.p8).
if ($ApiKey -and -not $KeyId -and ([IO.Path]::GetFileName($ApiKey) -match '^AuthKey_([A-Z0-9]{10})\.p8$')) { $KeyId = $Matches[1] }

$apiKeyText = if ($ApiKey) { Get-Content -LiteralPath $ApiKey -Raw } else { '' }
$result = Publish-CmSigningSecrets -KeyPem (Get-Content -LiteralPath $keyFile -Raw) `
    -CertificateBytes ([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Certificate).Path)) `
    -ProfileBytes ([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ProvisioningProfile).Path)) `
    -ApiKeyText $apiKeyText -KeyId $KeyId -IssuerId $IssuerId -Repo $Repo -Environment $Environment -BundleId $BundleId
exit $result.ExitCode
