# CollectionMaxxing

A personal collection tracker for physical media: games, books, films, music.
Point the iPhone at a shelf, and the collection catalogs itself.

## What it does

- **Scan.** The iPhone reads barcodes and photographs shelves, cases, and loose
  cartridges. Results land on a receipt, then move to the library in one review
  pass. Camera and recognition are iPhone-only and on-device (Vision +
  Foundation Models); the catalog lookup is the only network step.
- **Browse and manage.** The library grid and table, item details, locations
  ("Game room › Shelf 1"), lending, and a wishlist. The web app covers all of
  this; the phone adds scanning.
- **Share a household library.** One library, up to 3 libraries per account,
  with Owner / Editor / Viewer roles. Members join by invite link, sign in
  with Google or Apple, and browse from anywhere on the tailnet.

## Platforms

- **iPhone app** (Expo, shipped via TestFlight): scanning, library, everything.
- **Web app** (the same Expo codebase, `expo export`): browsing and managing.
  No camera, no recognition — **+ Add item** opens typed search or manual entry.

## Voice in one line

A knowledgeable collector friend: plain US English, verb-first buttons with
counts ("Add 11 items"), personality in empty states and success, clarity in
errors. Full rules live in [DESIGN.md](DESIGN.md).

## Non-goals (do not build these)

- No market value, no price paid, no pricing of any kind.
- No push notifications, no Sign in with Apple beyond Auth0, no capabilities
  the plan does not already use.
- No paid services or dependencies beyond the Apple Developer Program.

## Where things live

- `app/src/app/` — Expo Router routes (URL structure).
- `app/src/features/` — one folder per feature, screens plus logic.
- `app/src/ui/` — the shared primitives. New screens compose these.
- `app/src/theme/tokens.json` — the design tokens, single source of truth.
- `src/` — the .NET API; `contracts/openapi.json` — the API contract.
- `docs/` — the full plan (lead-owned).
