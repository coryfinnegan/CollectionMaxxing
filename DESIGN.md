# CollectionMaxxing Design System

The shipped Phase 0 system (#302). This file is the authority for how the app
looks, reads, and feels. It outranks the generic agent skills: where a skill
suggests something this file bans, this file wins.

## Tokens

`app/src/theme/tokens.json` is the single source of truth. `tokens.ts` only
resolves roles from it. Nothing invents a color anywhere else.

- **Neutrals:** warm Radix Sand 12-step scales, dark-first (`sandDark` on dark,
  `sand` on light).
- **Accent:** Amber. **Sticker** (badges, highlights): Orange. **Danger:** Red.
- **Roles** (every component reads these through `useColors()`): `ground`,
  `surface`, `ink`, `muted`, `line`, `control`, `accent`, `onAccent`,
  `sticker`, `stickerText`, `danger`, `receiptInk`, `receiptMuted`.
- **Fixed** (scheme-independent): `laser` `#FF2D3A` (scan beam), `receipt`
  `#FBFBF7`, `black`, `white`, `scrim` `rgba(0,0,0,0.5)`.
- **Spacing:** xs 4, sm 8, md 12, lg 16, xl 24, xxl 32. **Radius:** sm 6,
  md 10, lg 16, pill 999.
- Touch targets are at least 44pt. `WIDE_BREAKPOINT` (768) switches phone
  layouts to the wide (sidebar) layout.

To change the palette, edit `tokens.json` and this file together, in one PR.

## Type

Bricolage Grotesque for display and UI (400/600/700/800), Space Mono for
catalog metadata. Loaded by `fonts.ts` before the first screen; Dynamic Type
still scales.

| Cut      | Size / line | Weight | Family                       | Use                                    |
| -------- | ----------- | ------ | ---------------------------- | -------------------------------------- |
| display  | 34 / 40     | 800    | BricolageGrotesque_800ExtraBold | Hero moments (sign-in, welcome)     |
| title    | 28 / 32     | 800    | BricolageGrotesque_800ExtraBold | Page titles (web)                   |
| heading  | 20 / 26     | 700    | BricolageGrotesque_700Bold      | Section and card titles             |
| body     | 16 / 22     | 400    | BricolageGrotesque_400Regular   | Default reading text                |
| label    | 14 / 18     | 600    | BricolageGrotesque_600SemiBold  | Buttons, chips, small actions       |
| caption  | 13 / 17     | 400    | BricolageGrotesque_400Regular   | Hints, timestamps                   |
| mono     | 13 / 17     | 400    | SpaceMono_400Regular            | Catalog facts: format, platform, year |
| castleDisplay | 28 / 34 | 700    | Cinzel_700Bold                  | Castle frame display titles, 20pt+ only |

Reach for the `Text` primitive with a `variant`, never a bare `RNText` with
a hand-rolled style.

## Components

`app/src/ui/` holds the shared primitives: Button, Card, Chip, Cover,
EmptyState, FormatBadge, ListRow, LoadingState, Screen, Sheet, Snackbar,
Text, TextField, plus `usePressFeedback`.

- Compose screens from primitives. A repeated view earns promotion into `ui/`.
- Button takes a `label` prop and a `variant` (primary / secondary / danger);
  it owns its press feedback and haptic. Do not wrap button text by hand.
- Loading and empty branches are designed states: `LoadingState` and
  `EmptyState`, not blank screens or bare spinners.
- Press feedback is uniform: Button, Chip, and ListRow shrink and dim through
  `usePressFeedback` (Reanimated). New pressables use the same hook.

## Titles: one per screen

The navigator header names phone screens, so in-screen titles stay off the
phone: `Screen` renders its `title` on the web only, unless `showTitleOnPhone`
opts back in (headerless routes and content titles like an item's name).
Pushed screens keep a nav-bar title on both platforms and pass no `title` at
all. Subtitles are content, not titles, and always show.

## Castle theme (Phase 0)

The castle is an opt-in frame; the app inside stays clean. Phase 0 builds the
system only: no screen is themed yet.

- **Palette:** `tokens.json` `castle` is scheme-independent fixed color, like
  the receipt: `parchment`/`parchmentInk`, `stone`/`stoneInk`, `brass`,
  `iron`/`ironInk`, `olive`. Brass and olive are accents and graphics only,
  never body text.
- **Textures** (`app/assets/castle/`) are small tileable / 9-slice images
  generated from the palette bases (`app/scripts/castle-textures.py`) with
  baked contact shadows. No large background images; no text baked into any
  texture.
- **Primitives** (`app/src/ui/castle/`): CastleHeader, TabletHeader,
  BannerMarker, LedgeRow, ParchmentCard. Each renders in Full / Flat /
  Accessible modes; the mode resolves from the Theme setting (Full/Quiet)
  plus system Increase Contrast and Reduce Motion, with Accessible winning
  conflicts.
- **Contrast** is measured at the worst-case pixel under the text
  (`app/scripts/castle-contrast.mjs`): 4.5:1 body text, 3:1 large text and
  UI components.
- **Type:** the `castleDisplay` cut (Cinzel 700, OFL) is for castle frame
  display titles at 20pt and above only. Body and UI text stay Bricolage.

## Haptics

One vocabulary in `app/src/lib/haptics.ts` (silent on the web):

| Call      | Meaning                                              |
| --------- | ---------------------------------------------------- |
| scanned   | A code was read (instant tick, no network involved)  |
| owned     | "You own this" — two short taps                      |
| isNew     | Found, not in the library yet — one tap              |
| notFound  | The catalog has nothing — one long tap               |
| select    | Tab and filter changes — a selection tick            |
| tap       | Ordinary presses — a light tap                       |
| commit    | Save, add, delete — a medium tap                     |
| succeed   | Finished jobs — success, used sparingly              |

## Voice

- Plain US English. Sentence case everywhere.
- Buttons name the outcome with a count, verb first: **Add 11 items**,
  **Review 40 items**, **Scan your first shelf**. Never "Submit" or "Done".
- Personality lives in empty states and success ("Nice scan!"). Errors stay
  clear: what happened and what to do next ("Couldn't reach the catalog. Your
  scans are saved and will finish when you're back online.").
- No exclamation marks, no apologies, no "Oops". No ALL-CAPS except inside
  the receipt, where it is the receipt's own voice.

## Banned patterns

These fail the gate or the visual check. Do not introduce them; do not copy
them from grandfathered files.

- **Raw color literals** (`#fff`, `rgb(...)`) outside `tokens.json`. Use
  `useColors()` roles and `fixed`.
- **Doubled titles**: an in-screen title repeating the navigator header.
  Pushed screens pass no title; section titles render on the web only.
- **British spellings** in strings, comments, and identifiers (`colour`,
  `favourite`, `catalogue`, `cancelled` copy — the Auth0 error and camera
  `torchMode` API are documented exceptions).
- **Gradients, especially purple ones.** The system has no gradients and no
  gradient library. Do not add one.
- **Off-stack UI libraries**: no Tailwind/NativeWind, no `@expo/ui`, no
  bottom-sheet or picker replacements. Inline `style={{...}}` objects are
  banned in new files (older files are grandfathered, not examples).

## Enforcement

- `npm run check` in `app/` runs the banned-words scan and the design ratchet
  (no raw colors, no inline styles, no raw text, no doubled titles) as errors
  on new and changed files. Grandfathered violations live in
  `app/design-baseline.json` with a follow-up note, and the count may only
  go down.
- The Playwright visual spec screenshots the key screens against committed
  snapshots; UI-touching PRs must keep them green or deliberately update them.
- The `collection-design` agent skill points every UI session back here.

## References

- The Flippr scanner reel: aim, instant "Nice scan!" check, keep shooting
  while photos process, results as swipeable cards with cover art over the
  camera, tap a card for the boxed-spine detail. (Its pricing UI was
  deliberately not adopted — this app has no pricing.)
- Apple HIG behavior for native-feel details, via the `expo-native-ui` skill.
- `docs/06-ux-and-wireframes.md` holds the wireframes and key flows.
