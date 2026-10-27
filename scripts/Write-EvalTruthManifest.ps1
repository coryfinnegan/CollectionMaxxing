<#
.SYNOPSIS
  Seals the home-acceptance ground truth (issue #348) so commit order proves it predates the run.

.DESCRIPTION
  Truth files hold real titles and live on the PC only, so they cannot be committed. This script validates the filled
  acceptance truth files (no SLOT- placeholders left, N = 25-40 across the photos, the series/omnibus/worn-spine
  composition present) and writes a manifest of their SHA-256 hashes, which holds no titles and is committed BEFORE the
  run. After the run, -Verify re-hashes the truth files and fails when any changed since sealing.

  Write:   pwsh scripts/Write-EvalTruthManifest.ps1 -ShelfFolder <golden>\shelf -PhotoId accept-shelf-01 -Out evals/acceptance/<date>-truth-manifest.json
  Verify:  pwsh scripts/Write-EvalTruthManifest.ps1 -Verify -ShelfFolder <golden>\shelf -Manifest evals/acceptance/<date>-truth-manifest.json

  Needs PowerShell 7.
#>
[CmdletBinding(DefaultParameterSetName = 'Write')]
param(
  [Parameter(Mandatory, ParameterSetName = 'Write')]
  [Parameter(Mandatory, ParameterSetName = 'Verify')]
  [string]$ShelfFolder,

  [Parameter(Mandatory, ParameterSetName = 'Write')]
  [string[]]$PhotoId,

  [Parameter(Mandatory, ParameterSetName = 'Write')]
  [string]$Out,

  [Parameter(Mandatory, ParameterSetName = 'Verify')]
  [switch]$Verify,

  [Parameter(Mandatory, ParameterSetName = 'Verify')]
  [string]$Manifest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validId = '^[A-Za-z0-9_-]{1,64}$'

function Read-Truth([string]$Path) {
  # The scorer allows full-line // comments; strip those before parsing. Anything else must be strict JSON.
  $text = (Get-Content -LiteralPath $Path -Raw) -split "`r?`n" | Where-Object { $_ -notmatch '^\s*//' } | Join-String -Separator "`n"
  try {
    return $text | ConvertFrom-Json -ErrorAction Stop
  }
  catch {
    throw "Not valid truth JSON: $Path ($($_.Exception.Message))"
  }
}

function Get-TruthPath([string]$Id) {
  if ($Id -notmatch $validId) {
    throw "Not a valid photo id (letters, digits, _ and -, up to 64 characters): $Id"
  }
  $path = Join-Path $ShelfFolder "$Id.truth.json"
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Truth file not found (write ground truth first): $path"
  }
  return $path
}

# Validates one filled file. Returns its item/unreadable counts plus the composition it declares.
function Test-Truth([string]$Id) {
  $path = Get-TruthPath $Id
  $truth = Read-Truth $path
  # Property reads go through PSObject: under StrictMode a missing key must give our message, not a property error.
  $itemsProp = $truth.PSObject.Properties['items']
  $items = if ($null -ne $itemsProp -and $itemsProp.Value -is [Array]) { @($itemsProp.Value) } else { @() }
  if ($items.Count -eq 0) {
    throw "$path : fill in the items (the shelf totals N = 25-40)."
  }
  for ($i = 0; $i -lt $items.Count; $i++) {
    $titleProp = $items[$i].PSObject.Properties['title']
    $title = if ($null -ne $titleProp) { $titleProp.Value } else { $null }
    if ([string]::IsNullOrWhiteSpace([string]$title)) {
      throw "$path : item $($i + 1) has no title."
    }
    if ([string]$title -match 'SLOT-') {
      throw "$path : item $($i + 1) still has a template placeholder: $title"
    }
  }
  $unreadableProp = $truth.PSObject.Properties['unreadable']
  $unreadable = if ($null -ne $unreadableProp) { $unreadableProp.Value } else { $null }
  if ($null -ne $unreadable -and (($unreadable -isnot [int] -and $unreadable -isnot [long]) -or $unreadable -lt 0)) {
    throw "$path : unreadable must be 0 or more."
  }
  $slots = @{ Series = @(); Omnibus = @(); Worn = @() }
  $acceptProp = $truth.PSObject.Properties['_acceptance']
  if ($null -ne $acceptProp -and $null -ne $acceptProp.Value) {
    $accept = $acceptProp.Value
    $nameProp = $accept.PSObject.Properties['seriesName']
    $seriesName = if ($null -ne $nameProp) { [string]$nameProp.Value } else { '' }
    $seriesProp = $accept.PSObject.Properties['seriesSlots']
    foreach ($slot in @(if ($null -ne $seriesProp) { @($seriesProp.Value) } else { @() })) {
      if ((($slot -isnot [int] -and $slot -isnot [long]) -or $slot -lt 1 -or $slot -gt $items.Count)) {
        throw "$path : seriesSlots must be 1-based positions in items."
      }
      if ($seriesName -match 'SLOT') {
        throw "$path : name the series in _acceptance.seriesName."
      }
      $slots.Series += $slot
    }
    foreach ($key in 'omnibusSlot', 'wornSpineSlot') {
      $slotProp = $accept.PSObject.Properties[$key]
      $slot = if ($null -ne $slotProp) { $slotProp.Value } else { $null }
      if ($null -ne $slot) {
        if ((($slot -isnot [int] -and $slot -isnot [long]) -or $slot -lt 1 -or $slot -gt $items.Count)) {
          throw "$path : $key must be a 1-based position in items."
        }
        $slots[$key -eq 'omnibusSlot' ? 'Omnibus' : 'Worn'] += $slot
      }
    }
  }
  return [pscustomobject]@{ Id = $Id; Path = $path; Items = $items.Count; Unreadable = [int]($unreadable ?? 0); Slots = $slots }
}

function Test-Composition($checked) {
  $series = @($checked | ForEach-Object { $_.Slots.Series }).Count
  $omnibus = @($checked | ForEach-Object { $_.Slots.Omnibus }).Count
  $worn = @($checked | ForEach-Object { $_.Slots.Worn }).Count
  $missing = @()
  if ($series -lt 3) { $missing += "a series of 3+ volumes in _acceptance.seriesSlots (found $series)" }
  if ($omnibus -lt 1) { $missing += 'an omnibus/box set in _acceptance.omnibusSlot' }
  if ($worn -lt 1) { $missing += 'a worn spine in _acceptance.wornSpineSlot' }
  if ($missing.Count -gt 0) {
    throw "The shelf is missing $($missing -join '; ')."
  }
}

if (-not (Test-Path -LiteralPath $ShelfFolder -PathType Container)) {
  throw "Folder not found: $ShelfFolder"
}
$ShelfFolder = (Resolve-Path -LiteralPath $ShelfFolder).ProviderPath

if ($Verify) {
  if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
    throw "Manifest not found: $Manifest"
  }
  $sealed = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json -ErrorAction Stop
  $failures = @()
  foreach ($photo in @($sealed.photos)) {
    $path = Get-TruthPath $photo.photoId
    $now = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($now -ne [string]$photo.sha256) {
      $failures += "$($photo.photoId): changed since sealing (truth must predate the run and stay fixed)"
    }
  }
  if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL $_" }
    exit 1
  }
  $when = ([DateTime]$sealed.createdAtUtc).ToUniversalTime().ToString('o')
  Write-Host "ok   truth unchanged since $when ($(@($sealed.photos).Count) file(s) verified)"
  return
}

$checked = @(foreach ($id in $PhotoId) { Test-Truth $id })
$total = ($checked | Measure-Object -Property Items -Sum).Sum
if ($total -lt 25 -or $total -gt 40) {
  throw "The shelf totals $total items; N must be 25-40."
}
Test-Composition $checked

$doc = [ordered]@{
  note = 'Home acceptance ground truth seal (#348). Photo ids and hashes only: no titles, safe to commit. Commit BEFORE the run.'
  createdAtUtc = [DateTime]::UtcNow.ToString('o')
  totalItems = $total
  photos = @($checked | ForEach-Object {
    [ordered]@{
      photoId = $_.Id
      sha256 = (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash.ToLowerInvariant()
      items = $_.Items
      unreadable = $_.Unreadable
    }
  })
}
$parent = Split-Path -Parent $Out
if ($parent -ne '' -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
  throw "Output folder not found: $parent"
}
if (Test-Path -LiteralPath $Out) {
  throw "Refusing to overwrite (delete it first if re-sealing): $Out"
}
$doc | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Out -Encoding utf8NoBOM
Write-Host "ok   sealed $total items across $($checked.Count) truth file(s); commit $Out BEFORE the run"
