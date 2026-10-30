#Requires -Version 7
<#
.SYNOPSIS
    Regenerates the bundled game product-code table (ADR-031) from libretro-database.

.DESCRIPTION
    Downloads the cartridge lists (metadat/serial) and the disc lists (metadat/redump) of libretro-database at one pinned
    commit, and writes one compact, sorted, gzipped table: one row per (normalised product code, platform, title).

        CODE <TAB> PLATFORM <TAB> TITLE        e.g.  SNS-8C <TAB> SNES <TAB> Clay Fighter

    The table is data under CC-BY-SA-4.0 (see src/CollectionMaxxing.Infrastructure/GameSerials/LICENSE). Nobody edits it by
    hand: change the pinned commit below (or pass -Sha), run this script, and review the regenerated file in a PR.

    Only the systems that PlatformNames (src/CollectionMaxxing.Domain/Catalog/Matching/PlatformNames.cs) knows are read; the
    rest are skipped. A libretro system is mapped to our canonical platform key in $Systems.

    The codes are normalised exactly as ProductCode.Normalize (src/CollectionMaxxing.Application/GameSerials/ProductCode.cs)
    does, and a test checks that every row is already in that form. Codes that are not shaped like a code (a bare number, say)
    are left out, since the server could never be asked for them.

.PARAMETER Sha
    The libretro-database commit to read. Pinned so the table is reproducible.

.PARAMETER OutFile
    Where the gzipped table goes. Defaults to the embedded resource of the Infrastructure project.

.PARAMETER CacheDir
    A folder to keep the downloaded .dat files in (created if missing; a file that is already there is reused, so a re-run is
    offline). By default a temporary folder is used and removed afterwards.

.EXAMPLE
    pwsh scripts/game-serials/Update-GameSerials.ps1
#>
[CmdletBinding()]
param(
    [string] $Sha = '77f15a815241163fe4af9631ebce4c3adc954eac',
    [string] $OutFile = (Join-Path $PSScriptRoot '..' '..' 'src' 'CollectionMaxxing.Infrastructure' 'GameSerials' 'game-serials.tsv.gz'),
    [string] $CacheDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# libretro directory, system file (without .dat), our canonical platform key (PlatformNames).
$Systems = @(
    @('serial', 'Atari - 5200', 'Atari 5200'),
    @('serial', 'Atari - 7800', 'Atari 7800'),
    @('serial', 'Atari - Jaguar', 'Atari Jaguar'),
    @('serial', 'Atari - Lynx', 'Atari Lynx'),
    @('serial', 'Coleco - ColecoVision', 'ColecoVision'),
    @('serial', 'NEC - PC Engine - TurboGrafx 16', 'TurboGrafx-16'),
    @('serial', 'Nintendo - Game Boy', 'Game Boy'),
    @('serial', 'Nintendo - Game Boy Color', 'Game Boy Color'),
    @('serial', 'Nintendo - Game Boy Advance', 'Game Boy Advance'),
    @('serial', 'Nintendo - Nintendo 64', 'N64'),
    @('serial', 'Nintendo - Nintendo Entertainment System', 'NES'),
    @('serial', 'Nintendo - Super Nintendo Entertainment System', 'SNES'),
    @('serial', 'Nintendo - Virtual Boy', 'Virtual Boy'),
    @('serial', 'Sega - 32X', 'Sega 32X'),
    @('serial', 'Sega - Game Gear', 'Game Gear'),
    @('serial', 'Sega - Master System - Mark III', 'Master System'),
    @('serial', 'Sega - Mega Drive - Genesis', 'Genesis'),
    @('serial', 'Sony - PlayStation Portable', 'PSP'),
    @('redump', 'Microsoft - Xbox', 'Xbox'),
    @('redump', 'Microsoft - Xbox 360', 'Xbox 360'),
    @('redump', 'Nintendo - GameCube', 'GameCube'),
    @('redump', 'Nintendo - Wii', 'Wii'),
    @('redump', 'Sega - Dreamcast', 'Dreamcast'),
    @('redump', 'Sega - Mega-CD - Sega CD', 'Sega CD'),
    @('redump', 'Sega - Saturn', 'Saturn'),
    @('redump', 'Sony - PlayStation', 'PS1'),
    @('redump', 'Sony - PlayStation 2', 'PS2'),
    @('redump', 'Sony - PlayStation 3', 'PS3'),
    @('redump', 'Sony - PlayStation Portable', 'PSP'),
    @('redump', 'The 3DO Company - 3DO', '3DO')
)

# The regions No-Intro and Redump put in the first bracket after a title: "Clay Fighter (USA)", "Foo (Europe) (En,Fr) (Rev 1)".
$Regions = @(
    'USA', 'Europe', 'Japan', 'World', 'Asia', 'Australia', 'Austria', 'Belgium', 'Brazil', 'Canada', 'China', 'Croatia', 'Czech',
    'Denmark', 'Finland', 'France', 'Germany', 'Greece', 'Hong Kong', 'Hungary', 'India', 'Ireland', 'Israel', 'Italy', 'Korea',
    'Latin America', 'Mexico', 'Netherlands', 'New Zealand', 'Norway', 'Poland', 'Portugal', 'Russia', 'Scandinavia', 'Singapore',
    'Slovakia', 'South Africa', 'Spain', 'Sweden', 'Switzerland', 'Taiwan', 'Turkey', 'UK', 'Ukraine', 'United Kingdom', 'Unknown',
    'Argentina', 'Chile', 'Colombia', 'Peru', 'Bulgaria', 'Romania', 'Thailand', 'Malaysia', 'Indonesia', 'Vietnam', 'Philippines'
)
$RegionAlt = ($Regions | ForEach-Object { [regex]::Escape($_) }) -join '|'
$RegionGroup = [regex]::new(" \((?:$RegionAlt)(?:, (?:$RegionAlt))*\)")
$Article = [regex]::new('^(.+), (The|A|An|Les|La|Le|El|Das|Der|Die|Il)$')

function ConvertTo-ProductCode([string] $Raw) {
    # Mirrors ProductCode.Normalize in the Application project. Keep the two in step; a test compares them on every row.
    if ([string]::IsNullOrWhiteSpace($Raw) -or $Raw.Length -gt 64) { return $null }

    $builder = [System.Text.StringBuilder]::new()
    $gap = $false
    $dot = $false
    foreach ($c in $Raw.Trim().ToCharArray()) {
        $n = [int]$c
        $isLetter = ($n -ge 65 -and $n -le 90) -or ($n -ge 97 -and $n -le 122)
        $isDigit = $n -ge 48 -and $n -le 57
        if ($isLetter -or $isDigit) {
            $prevIsDigit = $builder.Length -gt 0 -and [int]$builder[$builder.Length - 1] -ge 48 -and [int]$builder[$builder.Length - 1] -le 57
            if ($gap -and $builder.Length -gt 0 -and -not ($dot -and $prevIsDigit -and $isDigit)) { [void]$builder.Append('-') }
            $gap = $false
            $dot = $false
            [void]$builder.Append([char]::ToUpperInvariant($c))
        }
        elseif ($c -in @(' ', "`t", '-', '_', '.', '/') -or ($n -ge 0x2010 -and $n -le 0x2014)) {
            $dot = ($c -eq '.') -and -not $gap
            $gap = $true
        }
        else {
            return $null
        }
    }

    $code = $builder.ToString()
    if ($code.Length -eq 0) { return $null }

    if (-not $code.Contains('-')) {
        $m = [regex]::Match($code, '^([A-Z]{2,5})([0-9].*)$')
        if ($m.Success) { $code = $m.Groups[1].Value + '-' + $m.Groups[2].Value }
    }

    if ($code.Length -lt 5 -or $code.Length -gt 24) { return $null }
    $groups = $code.Split('-')
    if ($groups.Count -lt 2 -or $groups.Count -gt 6) { return $null }
    if ($groups[0] -notmatch '^[A-Z]{1,6}$') { return $null }
    foreach ($g in $groups[1..($groups.Count - 1)]) {
        if ($g.Length -lt 1 -or $g.Length -gt 8) { return $null }
    }

    return $code
}

function Split-SerialField([string] $Field) {
    # One field may hold several codes, separated by commas or spaces ("SNSP-YN SNSP-UY"). A bare run of letters is the start of a
    # code printed with a space in it ("MK 27055-50"), so it is joined to the token after it. A bracketed code is an alternative.
    $tokens = @($Field -split '[,;\s]+' | Where-Object { $_ })
    $out = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $tokens.Count; $i++) {
        $t = $tokens[$i]
        if ($t -match '^[A-Za-z]{1,6}$' -and $i + 1 -lt $tokens.Count) {
            $t = $t + ' ' + $tokens[$i + 1]
            $i++
        }
        $out.Add(($t -replace '^\(', '' -replace '\)$', ''))
    }
    return $out
}

function ConvertTo-Title([string] $Name) {
    $cut = $RegionGroup.Match($Name)
    if ($cut.Success) {
        $title = $Name.Substring(0, $cut.Index)
    }
    else {
        $script:NoRegion++
        $title = [regex]::Replace($Name, '(\s*\([^)]*\))+$', '')
    }

    # A bracket before the region that is only a product code ("Chessmaster, The (DMG-N5) (USA)") is not part of the title.
    $title = [regex]::Replace($title, '(\s*\([A-Z0-9]+(?:-[A-Z0-9]+)+\))+\s*$', '')
    # " ~ " joins two spellings of one title; the first one stands.
    $title = ($title -split ' ~ ')[0]

    $parts = foreach ($part in $title.Trim() -split ' - ') {
        $m = $Article.Match($part.Trim())
        if ($m.Success) { $m.Groups[2].Value + ' ' + $m.Groups[1].Value } else { $part.Trim() }
    }

    return ((($parts -join ': ') -replace '\s+', ' ').Trim())
}

function Read-Dat([string] $Path) {
    # clrmamepro text: game ( comment|name "Title (Region)" serial "CODE" rom ( ... serial "CODE" ) ). Yields title and serial fields.
    $inGame = $false
    $depth = 0
    $title = $null
    $gameSerials = $null
    $romSerials = $null
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        $t = $line.Trim()
        if (-not $inGame) {
            if ($t -eq 'game (') {
                $inGame = $true
                $depth = 1
                $title = $null
                $gameSerials = [System.Collections.Generic.List[string]]::new()
                $romSerials = [System.Collections.Generic.List[string]]::new()
            }
            continue
        }

        if ($depth -eq 1) {
            if ($t -eq ')') {
                $inGame = $false
                $fields = $romSerials
                if ($gameSerials.Count -gt 0) { $fields = $gameSerials }
                if ($title -and $fields.Count -gt 0) { [pscustomobject]@{ Title = $title; Serials = $fields.ToArray() } }
            }
            elseif ($t -match '^(?:comment|name)\s+"(.*)"$') {
                if (-not $title -or $t.StartsWith('comment')) { $title = $Matches[1] }
            }
            elseif ($t -match '^serial\s+"(.*)"$') { $gameSerials.Add($Matches[1]) }
            elseif ($t -match '^rom\s+\($') { $depth = 2 }
            elseif ($t -match '^rom\s+\(.*\)$' -and $t -match '\sserial\s+"([^"]*)"') { $romSerials.Add($Matches[1]) }
        }
        else {
            if ($t -eq ')') { $depth = 1 }
            elseif ($t -match '^serial\s+"(.*)"') { $romSerials.Add($Matches[1]) }
        }
    }
}

$tempCache = $false
if (-not $CacheDir) {
    $CacheDir = Join-Path ([System.IO.Path]::GetTempPath()) ('game-serials-' + [guid]::NewGuid().ToString('N'))
    $tempCache = $true
}

try {
    $rows = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $perPlatform = @{}
    $dropped = @{}
    $script:NoRegion = 0
    $baseUrl = "https://raw.githubusercontent.com/libretro/libretro-database/$Sha/metadat"

    foreach ($system in $Systems) {
        $dir, $name, $platform = $system
        $folder = Join-Path $CacheDir $dir
        New-Item -ItemType Directory -Force $folder | Out-Null
        $path = Join-Path $folder "$name.dat"
        if (-not (Test-Path $path)) {
            Invoke-WebRequest -Uri "$baseUrl/$dir/$([uri]::EscapeDataString("$name.dat"))" -OutFile $path
        }

        foreach ($game in Read-Dat $path) {
            $title = ConvertTo-Title $game.Title
            if ([string]::IsNullOrWhiteSpace($title) -or $title.Contains("`t")) { continue }
            foreach ($field in $game.Serials) {
                foreach ($serial in Split-SerialField $field) {
                    $code = ConvertTo-ProductCode $serial
                    if (-not $code) {
                        $dropped[$platform] = 1 + [int]$dropped[$platform]
                        continue
                    }
                    if ($rows.Add("$code`t$platform`t$title")) {
                        $perPlatform[$platform] = 1 + [int]$perPlatform[$platform]
                    }
                }
            }
        }
    }

    $sorted = [string[]]::new($rows.Count)
    $rows.CopyTo($sorted)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)

    # Two known games, so a parser that quietly stopped working fails here instead of shipping an empty table.
    foreach ($expected in "SNS-8C`tSNES`tClay Fighter", "SNS-ADNE-USA`tSNES`tDonkey Kong Country 2: Diddy's Kong Quest") {
        if (-not $rows.Contains($expected)) { throw "The table lacks the expected row: $expected" }
    }

    $outDir = Split-Path -Parent $OutFile
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $text = "#libretro-database $Sha`n" + (($sorted -join "`n") + "`n")
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($text)
    $file = [System.IO.File]::Create($OutFile)
    try {
        $gzip = [System.IO.Compression.GZipStream]::new($file, [System.IO.Compression.CompressionLevel]::SmallestSize)
        try { $gzip.Write($bytes, 0, $bytes.Length) } finally { $gzip.Dispose() }
    }
    finally { $file.Dispose() }

    Write-Host "libretro-database $Sha"
    foreach ($p in $perPlatform.GetEnumerator() | Sort-Object Name) {
        Write-Host ('{0,-18} {1,7} rows   ({2} codes not shaped like a code, left out)' -f $p.Key, $p.Value, [int]$dropped[$p.Key])
    }
    Write-Host ('{0,-18} {1,7} rows   {2:N0} bytes gzipped ({3:N0} bytes of text)   {4} titles with no region bracket' -f 'Total', $rows.Count, (Get-Item $OutFile).Length, $bytes.Length, $script:NoRegion)
}
finally {
    if ($tempCache -and (Test-Path $CacheDir)) { Remove-Item -Recurse -Force $CacheDir }
}
