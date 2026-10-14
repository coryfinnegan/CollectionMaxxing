<#
.SYNOPSIS
  Copies the newest .ipa from the latest GitHub release into the files volume (ipa/), after checking its SHA-256.

.DESCRIPTION
  The deployer runs this as its last step; you can also run it by hand. Without -Apply it only prints what it would do.
  A checksum mismatch (or a release with no checksum file) leaves the previous file in place and exits 1.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Root = 'C:\CollectionMaxxing',
    [string]$Repo = 'coryfinnegan/CollectionMaxxing',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Cm.Common.ps1')
. (Join-Path $PSScriptRoot 'Cm.Ipa.ps1')
. (Join-Path $PSScriptRoot 'Cm.Deploy.ps1')

$doApply = $Apply.IsPresent -and -not $WhatIfPreference
$ctx = New-CmContext -Root $Root -Repo $Repo
Initialize-Cm -Apply $doApply -LogFile $ctx.LogFile
$result = Invoke-CmIpaSync -Ctx $ctx
exit $result.ExitCode
