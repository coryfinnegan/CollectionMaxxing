<#
.SYNOPSIS
  Creates empty truth skeletons for every golden picture that has no truth yet.

.DESCRIPTION
  Point it at the golden folder (the one with shelf/ and single/ inside) or at one of those two folders. For each picture
  (.jpg, .jpeg, .heic) that has neither <id>.truth.json nor <id>.truth.draft.json it writes <id>.truth.draft.json with the
  fields to fill in. A draft is never used in scoring: fill it in, remove the "draft" line and the "note" line, and rename it
  to <id>.truth.json.

  It only creates files in the folder you pass, never overwrites or deletes anything, and skips links (a file or a shelf/single folder that is a link) and any file whose name
  is not a valid id (letters, digits, _ and -, at most 64 characters).

  Needs PowerShell 7.   Usage:  pwsh scripts/eval-truth-stub.ps1 D:\golden
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory, Position = 0)]
  [string]$Folder
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Folder -PathType Container)) {
  throw "Folder not found: $Folder"
}
$root = (Resolve-Path -LiteralPath $Folder).ProviderPath

# The sets to fill: shelf/ and single/ inside the folder, or the folder itself when it is one of them.
$sets = @()
foreach ($name in 'shelf', 'single') {
  $path = Join-Path $root $name
  if (-not (Test-Path -LiteralPath $path -PathType Container)) { continue }
  # A set folder that is a link (junction or symbolic link) could lead outside the golden folder: never follow it.
  if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    Write-Warning "Skipping $path : it is a link, not a plain folder."
    continue
  }
  $sets += [pscustomobject]@{ Name = $name; Path = $path }
}
if ($sets.Count -eq 0) {
  $leaf = Split-Path -Leaf $root
  if ($leaf -in 'shelf', 'single') { $sets += [pscustomobject]@{ Name = $leaf; Path = $root } }
  else { throw "Expected a folder with shelf/ and single/ inside, or one of those two folders: $Folder" }
}

$picture = '.jpg', '.jpeg', '.heic'
$validId = '^[A-Za-z0-9_-]{1,64}$'
$note = 'Fill this in from the picture, remove the draft and note lines, then rename the file to <id>.truth.json.'

function Get-Skeleton([string]$set) {
  if ($set -eq 'shelf') {
    # items in order, left to right or top to bottom; mediaType is book, film, tv or game; unreadable counts items you can see but not read
    [ordered]@{ draft = $true; note = $note; items = @(); unreadable = 0 }
  }
  else {
    # format is a release format such as game_cartridge, vhs, dvd, paperback; completeness is complete, cart_only, disc_only, box_only, no_manual or n_a
    # kind is one of book_spine, game_box, disc_cover, vhs_sleeve, loose_cartridge, bare_label
    [ordered]@{ draft = $true; note = $note; title = ''; platform = $null; format = ''; completeness = ''; edition = $null; kind = '' }
  }
}

$created = 0
$skipped = 0
foreach ($set in $sets) {
  $files = Get-ChildItem -LiteralPath $set.Path -File | Where-Object {
    $_.Extension.ToLowerInvariant() -in $picture -and $_.BaseName -match $validId -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint)
  }
  foreach ($file in $files) {
    $truth = Join-Path $set.Path "$($file.BaseName).truth.json"
    $draft = Join-Path $set.Path "$($file.BaseName).truth.draft.json"
    if ((Test-Path -LiteralPath $truth) -or (Test-Path -LiteralPath $draft)) { $skipped++; continue }
    $json = Get-Skeleton $set.Name | ConvertTo-Json -Depth 4
    # CreateNew, so a file written meanwhile is never replaced; UTF-8 without a BOM.
    $stream = [IO.File]::Open($draft, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
      $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
      $stream.Write($bytes, 0, $bytes.Length)
    }
    finally { $stream.Dispose() }
    $created++
  }
}

Write-Host "Created $created truth skeleton(s); $skipped picture(s) already had a truth or draft file."
