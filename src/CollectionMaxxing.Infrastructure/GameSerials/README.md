# Game product codes

`game-serials.tsv.gz` maps a game's printed product code to its title and platform: `SNS-8C` is Clay Fighter on the SNES,
`SNS-ADNE-USA` is Donkey Kong Country 2: Diddy's Kong Quest, `SLUS-00594` is Metal Gear Solid on the PlayStation. The scanner uses it
when a read carries a code (ADR-031, `docs/12-decision-log.md`). It is embedded in the Infrastructure assembly and loaded once, on the
first lookup.

The table is **data under CC-BY-SA-4.0**, not our code. Attribution and share-alike apply to the table and to anything you copy out of
it; they do not apply to the rest of this repository. See [LICENSE](LICENSE).

## Source

- **Origin:** [libretro-database](https://github.com/libretro/libretro-database), by the libretro contributors, licensed
  [CC-BY-SA-4.0](https://creativecommons.org/licenses/by-sa/4.0/).
- **Files read:** `metadat/serial/*.dat` (cartridge systems) and `metadat/redump/*.dat` (disc systems), in the clrmamepro text format.
- **Pinned commit:** `77f15a815241163fe4af9631ebce4c3adc954eac` (the repository's `master` of 2026-10-30).
- **Generated:** 2026-10-30, by `scripts/game-serials/Update-GameSerials.ps1`.

## Changes made to the source data

This is an adapted copy, as CC-BY-SA asks us to say. Per row (one normalised code, one platform, one title) the script:

- keeps only the systems `PlatformNames` knows (the libretro system is mapped to our platform key) and leaves the rest out;
- spells each code one way (`ProductCode.Normalize`): upper case, one hyphen between groups, no dot inside a PlayStation disc's digits;
- leaves out codes that are not shaped like a code: a bare number (`1234`) or a code that starts with digits (`610-7132-50`);
- cuts a title at its region bracket, so "Clay Fighter (USA)" and "Metal Gear Solid (USA) (Disc 1)" become "Clay Fighter" and "Metal Gear Solid";
- turns " - " into ": ", moves a trailing ", The" to the front, and keeps the first of two spellings joined by " ~ ";
- splits a field that lists several codes, and drops rows that repeat.

## Contents

| Platform | Rows | | Platform | Rows |
| --- | ---: | --- | --- | ---: |
| 3DO | 388 | | N64 | 743 |
| Atari 5200 | 46 | | NES | 1,683 |
| Atari 7800 | 58 | | PS1 | 12,716 |
| Atari Jaguar | 9 | | PS2 | 12,907 |
| Atari Lynx | 1 | | PS3 | 5,528 |
| ColecoVision | 29 | | PSP | 5,009 |
| Dreamcast | 1,321 | | Saturn | 1,952 |
| Game Boy | 1,116 | | Sega 32X | 14 |
| Game Boy Advance | 2,353 | | Sega CD | 320 |
| Game Boy Color | 1,030 | | SNES | 2,635 |
| Game Gear | 180 | | TurboGrafx-16 | 347 |
| GameCube | 2,080 | | Virtual Boy | 23 |
| Genesis | 600 | | Wii | 3,706 |
| Master System | 106 | | Xbox | 1,566 |
| | | | Xbox 360 | 2,203 |

60,669 rows in all; 685,163 bytes gzipped.

## Format

Gzipped UTF-8 text, one row per line, sorted by code, then platform, then title (ordinal). Fields are separated by a tab:

```
SNS-8C<TAB>SNES<TAB>Clay Fighter
```

The first line, `#libretro-database <sha>`, names the commit the table was made from. The platform is a key `PlatformNames` knows (a test
checks that). A code is found as read, without a region suffix (`SNS-8C-USA` finds `SNS-8C`), or with one (`SNS-ADNE` finds
`SNS-ADNE-USA`); a code that names several games returns all of them.

## Regenerating

Nobody edits the table by hand. To move to a newer libretro-database commit:

1. Pick the commit (`gh api repos/libretro/libretro-database/commits/master --jq .sha`) and set it as the default `-Sha` in the script.
2. Run `pwsh scripts/game-serials/Update-GameSerials.ps1` from the repository root (PowerShell 7, network access; the output is the same
   every time for one commit).
3. Update the commit, date and numbers above, and open a PR with the regenerated file. The Infrastructure tests check the table
   (`GameSerialTableTests`).
