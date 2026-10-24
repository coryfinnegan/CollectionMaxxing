# 00 — Product: vision, scope, releases

## Vision

Cataloguing a physical media collection should take about as long as carrying it past a checkout scanner. The app does the identifying. You confirm.

## Who it's for

- **You first:** a collector with a lot of books, VHS, DVDs/Blu-rays and video games across shelves, bins and closets.
- **Your household (v1):** anyone you share a library with, by invite link or short code, as **Editor** (scan, add, edit) or **Viewer** (browse, and check "do I own this?").
- **Everyone else:** CollectionMaxxing is a normal multi-user web app. Anyone can sign in with Google or Apple, create libraries (up to 3) and share them. It is to be hosted as a product later; the PC only hosts it while we build it, because that is free ([ADR-027](12-decision-log.md#adr-027-a-multi-user-web-app-the-pc-is-only-a-development-host)). There is no server admin, setup code or server settings in the app.

## Jobs to be done

| # | When I… | I want to… | So that… |
|---|---|---|---|
| J1 | first set up the app | catalogue a whole shelf or bin in minutes | the initial setup doesn't take weeks |
| J2 | buy or receive something | add it in about 2 seconds | the catalogue stays current |
| J3 | am at a thrift store, garage sale or game shop | check whether I already own something | I don't buy duplicates |
| J4 | look for something | find it and know which shelf or bin it's in | I can actually get to it |
| J5 | lend something out | record who has it | I get it back |
| J6 | want to browse or show off | see my collection as cover art, filtered by format/platform | it's fun to use |
| J7 | worry about losing the data | export everything | the data is mine |
| J8 | live with other people who buy media too | share one library with them | nobody in the house buys a second copy |

## One scanner: point it at anything

There are no modes. The scanner looks at what's in front of it and does the right thing:

1. **Multi-barcode, live.** Lay items barcode-up (for example 6–10 game cases on the floor) and hold the phone over them. Every barcode in each camera frame is decoded on the device. Each new one gives a haptic tick and lands on the receipt tray.
2. **Sweep.** Keep the scanner open and sweep across a stack. Codes are de-duplicated within the session, so you can't double-add by accident. Tapping **+1** on a tray line records a genuine second copy.
3. **One item with no barcode → automatic picture (on-device text + artwork matching).** Hold up the item and keep it steady:
   - a loose N64 or Game Boy cartridge
   - a VHS sleeve or a bare tape
   - a game box, a disc, a cover

   The phone snaps it by itself (a barcode in view always wins), then identifies it from the picture. It reads the label, uses the cartridge shape for the platform, and compares the artwork against known covers. All of that runs on the iPhone, so it's free and the photo stays on your phone. You don't wait: move to the next item and each one resolves onto the receipt in a few seconds. A box of 10 loose carts takes about a minute.
4. **Many items → automatic picture of the whole shelf (AI).** Point at a shelf of spines or a floor spread of covers and hold steady. The phone reads every legible spine and cover itself (typically 10–30 per photo), with no cloud AI involved. This is the path for books on shelves and rows of VHS tapes.

Every result also says whether **you already own it**, so the same scanner answers "do I own this?" at a thrift store. Nothing is added until you tap **Add**.

Barcodes give exact matches down to the edition and format. Single-item pictures reliably give the title and platform, and the artwork match usually pins down the edition too (for example the *Player's Choice* re-release vs the original). Shelf pictures give title-level matches that can be unsure about the edition. The review screen shows that uncertainty, and you can refine the edition later.

## Scope by release

### v1.0 (MVP: self-hosted, installed on your iPhone through TestFlight)

- Sign in with Google or Apple on the web; the first sign-in creates your account. Create a library or join one with an invite link or code ([15](15-onboarding.md)). The iPhone signs in the same way, with an optional Face ID app lock
- **Libraries:** each person can own up to 3 and belong to others. Books, VHS, DVD, Blu-ray, 4K UHD, video games (+ "other")
- **Sharing:** invite people as **Editor** or **Viewer** by link or short code; each has their own account and iPhone; items show who added them
- **One scanner, no modes:** barcodes (many per frame, continuous) plus automatic pictures when no barcode is in view, all onto one receipt tray; works offline and resolves when back online
- **Single-item pictures (on-device text + artwork matching):** loose cartridges, VHS sleeves and tapes, boxes, discs; your photo shown beside the matched artwork; "use my photo" as the cover
- **Shelf pictures (on-device):** shelves, stacks, floor spreads; results stream in as cards; tap one to see **that item boxed on your photo** so you can find it on the shelf
- **Import from Photos:** run shelf pictures you already took through the same pipeline
- **Review and commit:** matched / ambiguous (pick from top 3) / not found (search manually); bulk set location and status
- **Ownership on every result** ("You own this: Shelf B"), so the scanner doubles as "do I own this?" at the thrift store, loose carts included
- Manual search-and-add from catalogue providers; fully manual entry as a last resort
- Library browse: grid of covers, search, filters (type, format, platform, location, status, tag), sort
- Item detail and edit: condition, completeness (CIB / loose / sealed…), location, notes, tags, acquired date, status (Owned / Wishlist / Lent / Sold)
- Locations (for example "Living room › Shelf 2", "Garage › Bin 4")
- Web app for **browsing and managing** the library: table view, bulk edit, search, locations, export. **No scanning or recognition on the web**; that's iPhone-only.
- Export (CSV + JSON) and account deletion
- **Offline "do I own this?"**: the phone keeps a synced copy of what you own, so checks work even when the PC at home is off or unreachable

### v1.1

- CSV import (generic + Goodreads/CLZ/LibraryThing exports)
- Lending tracker with reminders
- Stats (counts by format, platform, decade; recently added)

### v2 (ideas, not committed)

- ~~Collection value tracking~~: parked; you decided no pricing for now (2026-10-12)
- Public read-only share links ("see my N64 collection")
- Android (Expo makes this cheap)
- Music: CD, vinyl, cassette via Discogs
- iOS widgets and App Intents ("Hey Siri, do I own Metroid Prime?")

## Non-goals (for v1)

- Scanning or recognition in the web app (iPhone only, Apple frameworks only)

- A marketplace or social network
- Digital libraries (Steam, Kindle, Plex). Physical media only.
- Pricing of any kind (market value, price paid, appraisal, insurance): not planned. This is a collection tracker (your call, 2026-10-12).
- Android

## Success measures

| Measure | Target |
|---|---|
| Time to add one barcoded item (scan to committed) | under 3 s on a warm connection |
| Throughput, multi-barcode batch of 10 | under 30 s including review |
| Barcode match rate (auto-matched, no user fix) | ≥ 90% books, ≥ 75% movies/games (UPC data is patchier) |
| Shelf-photo recall (items found vs. items readable) | ≥ 85% on the eval set |
| Shelf-photo precision (correct title) | ≥ 90% on the eval set |
| Single-item picture top-1 accuracy (default, no LLM) | ≥ 88% boxes/covers, ≥ 80% VHS sleeves, ≥ 70% loose cartridges (eval set) |
| AI cost per scan (default) | **$0**: recognition runs on the phone |
| Single-item picture time to answer | ≤ 2 s first time; instant (and offline) for a repeat of something you've confirmed before |
| "Do I own this?" answer time (barcode) | under 1.5 s online, instant offline |

The AI targets are measured by the eval suite in [09](09-testing-and-tdd.md#ai-evals). We tune prompts and models against them, not by gut feel.

## Glossary

| Term | Meaning | Example |
|---|---|---|
| **Work** | The creative thing, independent of format | *The Matrix* (1999 film) |
| **Release** | A specific physical product, usually with a barcode | *The Matrix* DVD, Warner, 1999, with its UPC |
| **Item** | Your copy of a release | Your DVD, "Good" condition, Shelf B |
| **Library** | The collection that items belong to; has members | "Cory's Library" |
| **Scan session** | One batch of scanning activity | "Garage bin 4, Saturday" |
| **Candidate** | One thing the scanner found, before you confirm it | a scanned UPC → *Super Mario 64* (N64), 98% match |
| **Provider** | An external metadata source | Open Library, TMDB, IGDB |
