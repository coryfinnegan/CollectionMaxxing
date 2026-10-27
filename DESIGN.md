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

Reach for the `Text` primitive with a `variant`, never a bare `RNText` with
a hand-rolled style.

## Components

`app/src/ui/` holds the shared primitives: Button, Card, Chip, Cover,
EmptyState, FormatBadge, ListRow, LoadingState, Screen, Sheet, Snackbar,
Text, TextField, plus `usePressFeedback`.

- Compose screens from primitives. A repeated view earns promotion into `ui/`.
- Button takes a `label` prop and a `variant` (primary / secondary / danger / onDark — onDark is fixed-white for dark surfaces over the camera);
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

## Appearance

One design in two schemes: Light, Dark, and System (follow the OS). The two
Unistyles themes are the Phase 0 tokens; the kept choice persists on the
device and the first paint reads it synchronously. There are no themed modes,
no alternate skins, and no mode switcher. The Mall, Castle, and Great Hall
themes were removed outright (#426); do not resurrect them without the
owner's explicit word.

## Mascot

The dragon is the app's character, and only the approved master artwork ships
— charming, not a theme. The #365 redrawn pose library was removed without
shipping (#426); no redrawn or off-model dragon appears anywhere in the app.
Any future placement of the master artwork follows these rules:

- The dragon appears reactively (state changes, empty/loading/error), never uninvited mid-task.
- Never on: the scanner/camera, forms while editing, search results, Settings, paywall, or delete/consent flows.
- At most one proactive mascot moment per session; no looping motion in peripheral vision while the user is reading or scanning.
- Decorative to VoiceOver unless the art conveys state; Reduce Motion always gets the static art.

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
