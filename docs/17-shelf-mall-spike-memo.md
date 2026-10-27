# Shelf / Mall design-spike memo (#357)

Prototype + decision memo for the three switchable library designs. The
prototype ships on main behind Settings → Appearance → Library design
(Current / Castle / Mall, default Current); the go/no-go per mode is Cory's
after testing on his phone. Issue #357 stays open for that decision.

## What shipped

- **The switcher** (`app/src/features/library/design.ts`, Settings Appearance
  section): Current stays the default grid; Castle and Mall render the same
  items, same sort, same VoiceOver order. The scan flow is untouched in every
  mode, and the web table is unchanged (the design applies to the grid view).
- **Castle Shelf** (`app/src/features/library/CastleShelf.tsx`): the grid order
  dealt onto stone ledges in shelf rows, built on the castle primitives
  (CastleHeader, BannerMarker, ledge art) with Full / Flat / Accessible modes
  plus the density guardrails (full theme at 0–1 items, frame-only at 2–12,
  covers + header beyond). Books lean on the shelf; Reduce Motion and
  Accessible mode still them. The shared cover transition into detail is
  reused, so pull-off-the-shelf works where the grid flight works.
- **Mall mode** (`app/src/features/mall/`): one store per kind (video store for
  movies, toy-store game department for games, bookstore for books, mall hub
  for everything), with room tabs, the Blender camera presets per room, and
  tap-to-identify through the `anchor_<section>_<n>` contract. Two canvases
  share one MallView: real-time 3D on the web (three.js, GLB anchor transforms
  with a procedural fallback) and a 2.5D room on the phone (Reanimated snap
  carousel in the room palette). An aisle list under the room carries every
  item in library order in both.
- **Toy-store 90s revision**: the ticket wall is gone. The games room is now a
  90s toy-store aisle — a game wall of cased games under a TOP 10 pelmet, a
  TRY ME demo kiosk, pegboard runs, the glass case. Generic words only, per the
  trademark guardrails. `toy_store.glb` regenerated with a fresh proof render;
  `anchor_ticket_*` became `anchor_wall_*` across the script, the GLB, the
  presets, `rooms.ts`, and the tests together.

Side-by-side capture: `app/playwright/screenshots/library-modes-side-by-side.png`
(grid | castle | mall), plus per-room close-ups (`rooms-hub/movies/games/books.png`)
and the regenerated Blender proof (`scripts/blender/renders/toy_store.png`).

## Routes evaluated (measured)

### Mall route A — three.js real-time 3D (web, shipped)

The web canvas builds a procedural shell per room in the Blender palette,
places generated cover tiles at the GLB anchor transforms, and offers orbit
(drag / wheel / pinch), the Blender camera presets with eased flights, and
raycast tap-to-identify. It reads only the GLB JSON chunk (no GLTFLoader),
so the rooms render even where mesh loading is not yet built.

Measured (Playwright, headless Chromium + SwiftShader software GL, 1280×800,
hub room, 40 covers, committed pacing probe in `mall.spec.ts`):

- 182 frames over 3 s, median frame 16.7 ms, p95 16.7 ms — a locked 60 fps
  even on software rendering.
- Runtime draws: 7 shell meshes + 1 per cover (40 in the shot → 47 draws, no
  instancing yet).
- Room files (`node app/scripts/mall-perf.mjs`, enforced by
  `rooms.glb.test.ts`, limits 150k tris / 40 draws):

  | Room | Size | Tris | Draws | Anchors |
  | --- | --- | --- | --- | --- |
  | video_store | 221 KB | 5056 | 17 | 9 |
  | toy_store | 269 KB | 6048 | 32 | 5 |
  | bookstore | 316 KB | 7268 | 18 | 5 |
  | mall_hub | 361 KB | 8768 | 29 | 5 |

- Cost: `three` (web bundle only — the native app never imports it) and
  `expo-asset` (SDK-pinned, resolves the room files). No new native modules.
  Bundle delta vs main, measured in #421 (`npm run build:web`, i.e.
  `expo export --platform web`, after `npm ci` + `npm run generate` on each
  side; spike at 9b30fea, parent at c585b92 via `git archive`):
  - `dist/` total 6,378,209 → 8,435,609 bytes (+2,057,400, +32%), 108 → 112
    files.
  - JS +862,212 bytes, all in the shared `index-*.js` bundle (3,039,181 →
    3,901,393; every other chunk byte-identical); three.js markers in that
    bundle 0 → 52. Gzip of the shared bundle 743,264 → 948,196 (+204,932).
  - Per route: 46 HTML files byte-identical on every route (1,012,427 bytes
    both sides) — the delta is the shared JS plus assets, not route shells.
  - Room GLBs +1,195,188 bytes in 4 on-demand asset files (mall_hub 369,280,
    bookstore 323,928, toy_store 275,580, video_store 226,400).
  - The JS delta (862,212) plus the GLBs (1,195,188) accounts for the total
    (2,057,400) exactly. Every web route loads the three.js weight even
    without visiting the Mall, so lazy-loading the 3D room on first Mall
    visit is follow-up #423.

CI calibration (PR #422 round 1): the same probe on CI's 2-core shared
runners (two specs in parallel) reports 40 frames over 3 s, median
83 ms, p95 100 ms. The deltas are even — no stall tail — and the
identical scene locks 60 fps on a fast CPU, so this is runner speed,
not a pacing regression: the steady-state frame does no per-frame
allocation (lerp + render), and software rasterization is purely
CPU-bound. The probe therefore detects software GL
(`WEBGL_debug_renderer_info`) and asserts steady animation there
(frames > 30, p95 < 250 ms — continuous rAF with no stalls) while
hardware GL keeps the rate floor (frames > 60). The phone story is
unaffected: the native canvas is Reanimated 2.5D with no GL surface at
all. Measured vs still-needed: this probe measures that the web canvas
animates continuously without stalls; the 60 fps feel on Cory's iPhone
(route B, risk 1) and the web feel on a real GPU still need his device.

### Mall route B — Reanimated 2.5D rooms (phone, shipped)

The native canvas is a snap carousel of anchor cards in the room palette with
preset-driven glides and tap-to-identify: no new native modules, no GL
surface, TestFlight-safe by construction. It shares MallView's tabs, presets,
identify card, and aisle list with the web canvas, so the modes cannot
disagree on data or order.

Measured (`perf.test.ts`, committed): dealing 500 items onto anchors costs
~0 ms per room (budget 250 ms). The lists window through the same FlatList
pagination as the grid (30 per page), and the room windows 8 covers per
anchor (at most 72 cover views in the largest room; the aisle list carries
the rest). Native frame pacing was NOT measured here — there is no iPhone in
this loop. That measurement is the point of shipping it to Cory's phone.

### Rejected for the spike (not for the product)

- **three.js via expo-gl on native**: the honest full-3D phone route, but it
  adds a native GL module whose TestFlight behavior and iPhone frame pacing
  this spike cannot verify. Costed as follow-up #414 instead of risked blind.
- **Skia-rendered shelves**: a second new native dependency plus a new
  rendering idiom for one screen, with no measured advantage over the two
  shipped routes. Kept as the fallback if expo-gl disappoints.
- **Reanimated + perspective 3D grid**: evaluated in miniature and dropped —
  static lean plus shared transitions carry the physicality cheaper (see the
  castle numbers below).

Recommendation: ship route B on the phone and route A on the web now; fund
the expo-gl follow-up only if Cory's go/no-go wants true 3D on the phone.
The scene graph is already asset-swappable behind `rooms.ts`, so Thursday's
Blender polish drops in without a rewrite either way.

## Castle vs plain (measured)

The castle shelf reuses the grid's data path (`useItems`, same filters, same
pagination) and the same FlatList virtualization — only the row renderer
changes. Dealing 500 items onto shelves costs 1.0 ms (`perf.test.ts`). The
density guardrails bound the decoration cost: 35 seed items render covers +
header with zero banner markers (pinned e2e), while ≤12 items get markers and
ledges. Sunlit vs torchlit falls out of the scheme: day art in light mode,
the torchlit-night generations in dark mode, measured by the existing
`castle-contrast` gate — no second variant to build.

The one deliberate cut: shelves are consecutive runs of the grid order, NOT
regrouped by location. Location grouping would reorder items against the
identical-sort-order guardrail, so the guardrail won. If Cory wants location
grouping anyway, that is follow-up #420 (needs his explicit call).

## Accessibility parity

- Same data, same sort, same VoiceOver order in all modes: pinned by jest
  order tests (grid vs shelf vs aisle, item for item) and e2e (first rows
  match across a mode switch). Playwright axe scans pass on the castle and
  mall pages with zero serious/critical findings.
- Reduce Motion: shelf lean off, preset flights jump instead of gliding,
  entering animations off (the shared `motion.ts` gates); the canvas orbit is
  direct manipulation and stays.
- 200% text: shelf books, the identify card, and the aisle list are real text
  and scale; the web 3D canvas is decorative to assistive tech (its aisle
  list carries the meaning), while the native 2.5D canvas exposes the same
  labeled covers as the list. That web/native split is deliberate and
  documented here; unifying it (keyboard-reachable covers on web) is
  follow-up #417.
- Contrast: room signs and generated cover tiles read from the token palette;
  the worst pair (Movies sign on cream) passes AA for large text.

## Risks and honest gaps

1. **Native 60 fps is unverified.** The 2.5D room is cheap by construction
   (snap carousel, static transforms, windowed covers), but "cheap" is not a
   measurement. Cory's feel report + the follow-up tuning issue close this.
2. **Web rooms are procedural shells**, not the GLB meshes: the Loader reads
   anchor transforms, not geometry. Full GLB mesh rendering is follow-up #415.
3. **Generated cover tiles, not cover art**, in both 3D rooms (spine band +
   title from the token palette). Real art textures are follow-up #416.
4. **Room tabs narrow client-side** over loaded pages; a 500-item Movies room
   with 30 loaded shows 30. Driving the server kind filter is follow-up #418.
5. **Visual snapshots are Chromium + SwiftShader renders.** Linux-vs-Windows
   rasterization has bitten before (0.015 tolerance); if CI disagrees with
   local on the new mall snapshot, that is a tolerance question, not a design
   one.
6. **The opening view is not exactly the hero preset**: rooms open facing
   their first full anchor down the aisle (covers, not wallpaper); the Hero
   chip keeps the exact Blender angle one tap away. Deliberate.
7. **Thursday's Opus Blender polish** lands on top of these assets; the seam
   test (`rooms.glb.test.ts`) fails loudly if anchors drift, so the polish
   either updates `rooms.ts` with the models or breaks the build — never
   silently.

## Follow-ups (filed, not started)

Costed `muse-task` issues, dependency-ordered. Nothing below starts without
Cory's go/no-go:

- #414 — Native true-3D Mall rooms (expo-gl route): replace the 2.5D canvas,
  verify TestFlight frame pacing on Cory's phone. Depends on: go for Mall.
- #415 — Render GLB room meshes on web (GLTFLoader): replace the procedural
  shells with the Blender rooms. Depends on: go for Mall.
- #416 — Real cover-art textures in 3D rooms (generated-tile fallback).
  Depends on: go for Mall.
- #417 — Keyboard-reachable covers + in-scene selection highlight on web.
  Depends on: go for Mall.
- #418 — Room tabs drive the server kind filter. Depends on: go for Mall.
- #419 — Measure and tune iPhone frame pacing (all modes). Depends on: Cory's
  feel report.
- #420 — Location-grouped castle shelves (reorders vs the guardrail — needs
  Cory's explicit call). Depends on: go for Castle.
- #421 — Measure the web bundle delta vs main. Depends on nothing. Done;
  numbers are in the route A section above.
- #423 — Lazy-load the web 3D room on first Mall visit (dynamic-import the
  three.js canvas so non-Mall routes keep the pre-spike bundle). Depends on:
  go for Mall.

## What Cory gets back to us

Posted as the how-to-test comment on #357: where the switcher lives, what to
try per mode, and what we need back — fps feel against his real library and a
go/no-go per mode. The prototype is the ballot; this memo is the briefing.
