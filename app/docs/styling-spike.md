# Styling-layer spike: Uniwind vs Unistyles 3 (#305)

Date: 2026-10-25. Branch: `muse/issue-305`. Status: **spike complete, decision recorded;
migration blocked on package install (see §12)**.

**Verdict: adopt `react-native-unistyles` 3.6.0.** It keeps `tokens.json` the single
source of truth, keeps the `cm-design` ratchet working unchanged, migrates
mechanically file by file, and ships an official jest story. Uniwind is rejected:
it duplicates the tokens into CSS, its class strings bypass every `cm-design`
rule, its best engine is a paid tier this repo cannot take, and it has no
documented jest story. `@expo/ui` is rejected too: its web support is
experimental while web is a first-class target, and the app has almost no native
controls for it to replace (one `Modal`, zero switches/pickers/sliders).

## 1. What this spike is (read this first)

The brief asks for the Library screen prototyped twice, judged on migration
effort, runtime behavior on iPhone + web, token enforcement, and test impact.
Two honest caveats:

- **The prototypes below are static, not executed.** Both contenders are new npm
  dependencies, and installing packages is the lead's job (hot files), so no
  prototype could run in this worktree. They are complete, runnable-after-install
  translations of every style site in `LibraryGrid`, written against the current
  docs (Uniwind 1.13, Unistyles 3.6) — enough to judge effort, token wiring, and
  test impact exactly, but not runtime feel.
- **No iPhone claims are made.** There is no device here; the repo's own flow is
  "try JS on the web, then in a TestFlight build" (no dev builds, no Expo Go in
  use). Runtime verification is specified as a post-install step (§8) and must
  actually run before the migration is called done.

## 2. The ground both stand on

Facts about this repo that decide the comparison more than vendor benchmarks:

- **Zero-cost rule.** The only paid item is the Apple Developer Program. A paid
  tier is not an option without a new decision.
- **`src/theme/tokens.json` is the single source of truth** (Radix Sand/Amber
  roles, spacing, radius, type). Phase 0 plus #302 built this; the winner must
  consume it, not fork it.
- **The `cm-design` ratchet** (`eslint-rules/cm-design.js`, enforced by
  `scripts/design-gate.mjs` against `design-baseline.json`) bans raw colors,
  statically-valued inline styles (#331: dynamic values stay inline because a
  module-scope StyleSheet cannot express them), raw text, doubled titles, and
  gradients. It inspects `style`/`contentContainerStyle` props and string
  literals. A layer whose styles are invisible to it needs new tooling.
- **Web is first-class**: static export (`expo export --platform web`), served
  under a strict CSP (`style-src 'self' 'unsafe-inline'` — both contenders'
  web output is inline styles/variables, so neither is blocked).
- **iPhone via TestFlight prebuilds**: `expo prebuild` regenerates native code
  on CI, so a library with native modules adds no new step. Nitro Modules
  (`react-native-nitro-modules` ^0.37.1) is already installed, and SDK 55+
  runs only on the New Architecture.
- **Pixel-identical migration is enforced**: `playwright/visual.spec.ts`
  snapshots six screens (tolerance 0.015) and `mobile.spec.ts` asserts computed
  colors. Either migration must keep these green unchanged.
- **No jest test asserts styles or snapshots** (verified by grep); but
  `entering.test.tsx` asserts component tree shape, so the migration must not
  restructure wrappers.

## 3. The contenders (pinned versions, 2026-10-25)

| | Uniwind (free tier) | Unistyles 3 |
|---|---|---|
| Package | `uniwind@1.13.0` + `tailwindcss@4.3.3`, realistically also `tailwind-merge` (+ `tailwind-variants`) | `react-native-unistyles@3.6.0` alone |
| License / cost | Free tier $0; the C++ engine, zero re-renders, group variants, Reanimated-via-className are **Pro, from $99/seat/yr** — forbidden here | MIT, full engine free |
| Peers needed | `tailwindcss >= 4`, RN >= 0.81 ✓, React >= 19 ✓ | RN >= 0.81 ✓, Reanimated ✓ (have 4.5.1), nitro-modules ✓ (have 0.37.1), `react-native-edge-to-edge` (see below) |
| Native code | None (free tier); works in Expo Go | Nitro C++ modules; needs a prebuild (already the flow); no Expo Go (unused here) |

`react-native-edge-to-edge` (a Unistyles peer) is **not needed**: it is
Android-only, this app is iOS + web, and the shipped 3.6.0 JS contains zero
references to it (verified by unpacking the tarball and grepping both
`lib/module` and `lib/commonjs`). Expect one npm peer warning, nothing more.
Unistyles 3.1+ enables edge-to-edge itself anyway.

## 4. Setup in this repo (file by file)

**Unistyles 3** (per the Expo Router + static-rendering guides):

1. `npm install react-native-unistyles@3.6.0` — **lead** (hot lockfiles).
2. `package.json`: `"main": "expo-router/entry"` → `"index.ts"` — **lead, hot
   file, one line**. Required: Unistyles parses StyleSheets at import time, and
   Expo Router loads route modules before any layout runs, so the documented
   entry (`index.ts` importing `expo-router/entry` then `./unistyles`) is the
   only supported way to guarantee `StyleSheet.configure` runs first.
3. `babel.config.js` — create (not hot): `babel-preset-expo` +
   `react-native-unistyles/plugin`. No babel config exists today; the plugin is
   a no-op under jest. The experimental Metro integration is explicitly not
   recommended over the plugin, so no `metro.config.js` is needed.
4. `index.ts` — create (not hot): two imports.
5. `src/theme/unistyles.ts` — create: themes built from `tokens.json`,
   breakpoints, `adaptiveThemes: true` (the app follows the system scheme
   today via `useColorScheme`), plus the `UnistylesThemes`/`UnistylesBreakpoints`
   module augmentation.
6. `src/app/+html.tsx` — create (not hot): Expo static rendering needs the
   config imported in the root HTML file. None exists today.
7. `jest.config.js` — edit (not hot): `setupFiles: ["react-native-unistyles/mocks",
   "./src/theme/unistyles"]` (mocks first, config second — documented order).
8. Nothing else: no native-config change, TestFlight prebuild autolinks.

**Uniwind** (per the quickstart + theming docs):

1. `npm install uniwind tailwindcss tailwind-merge` — **lead**. (`tailwind-merge`
   is required in practice: `Text`/`Button` merge base + tone + caller styles,
   and Uniwind does not dedupe conflicting classes, especially on web.)
2. `metro.config.js` — create (not hot): `withUniwindConfig(config,
   { cssEntryFile, dtsFile })`, outermost wrapper.
3. `global.css` — create (not hot): `@import 'tailwindcss'; @import 'uniwind';`
   plus the token wiring (§6). Location matters (it roots class detection);
   import it from the root layout, never the entry file.
4. `uniwind-types.d.ts` — **auto-generated by running the Metro server**; `tsc`
   needs it. A headless `generate-artifacts` step would have to slot into the
   gate/CI before typecheck. New moving part, new failure mode.
5. No babel, package.json, or native changes. No Expo Router entry change.

Setup is Uniwind's best round: no hot files, no native rebuild. It loses the
later rounds.

## 5. Prototype A: LibraryGrid on Unistyles 3

Config first — note the themes are the existing TS theme objects, so
`tokens.json` stays the only source:

```ts
// src/theme/unistyles.ts
import { StyleSheet } from 'react-native-unistyles';
import { darkColors, fixed, lightColors, radius, spacing, typeScale } from './tokens';

const shared = { spacing, radius, type: typeScale, fixed };
const appThemes = {
  light: { colors: lightColors, ...shared },
  dark: { colors: darkColors, ...shared },
};
const breakpoints = { phone: 0, wide: 768 }; // wide === WIDE_BREAKPOINT

type AppThemes = typeof appThemes;
type AppBreakpoints = typeof breakpoints;

declare module 'react-native-unistyles' {
  export interface UnistylesThemes extends AppThemes {}
  export interface UnistylesBreakpoints extends AppBreakpoints {}
}

StyleSheet.configure({ themes: appThemes, breakpoints, settings: { adaptiveThemes: true } });
```

Then the screen. All 15 style sites move to one module-scope sheet; the only
dynamic value (tile width) becomes a dynamic function; the `tileWidth`/`columns`
arithmetic keeps using the `spacing` import. `useColors` disappears from the
file:

```tsx
import { StyleSheet } from 'react-native-unistyles';
// ... same imports, minus useColors (spacing stays for the tile math)

const styles = StyleSheet.create((theme) => ({
  loading: { padding: theme.spacing.lg, gap: theme.spacing.lg },
  safe: { flex: 1, backgroundColor: theme.colors.ground },
  listContent: { padding: theme.spacing.lg, gap: theme.spacing.lg },
  column: { gap: theme.spacing.md },
  header: { gap: theme.spacing.md, paddingBottom: theme.spacing.md },
  headerRow: { flexDirection: 'row', alignItems: 'center', justifyContent: 'flex-end', gap: theme.spacing.md },
  title: { flex: 1 },
  chipRow: { gap: theme.spacing.sm },
  wrap: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing.sm },
  snackbarPad: { padding: theme.spacing.lg },
  tilePressable: { gap: theme.spacing.xs },
  tile: (width: number) => ({ width }),
}));

// JSX: style={styles.safe}, contentContainerStyle={styles.listContent},
// columnWrapperStyle={styles.column}, style={styles.tile(tileWidth)}, etc.
// Tree shape, props, testIDs, animation wrappers: untouched.
```

Effort read: mechanical. Move each object, `colors.X` → `theme.colors.X`,
`spacing.X` → `theme.spacing.X` inside the sheet; dynamic values become
functions. The file keeps its shape; reviewers diff style values 1:1.
`no-inline-styles` counts for the file drop to zero (dynamic functions live in
the sheet); `no-raw-colors` keeps enforcing because values are still TS
expressions, not strings.

## 6. Prototype B: LibraryGrid on Uniwind

Token wiring first. Spacing maps to Tailwind v4's dynamic scale
(xs/sm/md/lg/xl/xxl = 1/2/3/4/6/8 — implicit, must be memorized); radius and
fonts are static `@theme` tokens; the 17 color roles × 2 schemes become CSS
variables per the "CSS Variables with `@layer theme`" doc. **Every value below
is hand-copied from `tokens.json` — a second source of truth** (a codegen
script from `tokens.json` could emit it, which is itself a new build step to
own):

```css
/* global.css (excerpt; all 17 roles × light/dark in the real file) */
@import 'tailwindcss';
@import 'uniwind';

@theme {
  --radius-sm: 6px; --radius-md: 10px; --radius-lg: 16px; --radius-pill: 999px;
  --font-display: 'BricolageGrotesque_800ExtraBold', sans-serif;
  --font-body: 'BricolageGrotesque_400Regular', sans-serif;
  --font-mono: 'SpaceMono_400Regular', monospace;
}

@layer theme {
  :root {
    @variant light { --color-ground: #f9f9f8; --color-surface: #fdfdfc; /* … */ }
    @variant dark { --color-ground: #111110; --color-surface: #191918; /* … */ }
  }
}
```

Then the screen (all 15 sites; Uniwind supports `contentContainerClassName` and
`columnWrapperClassName`, verified in its component docs):

```tsx
// Loading branch:
<SafeAreaView edges={['left', 'right']} className="p-4 gap-4"> …
// Header:
<View className="gap-3 pb-3">
  <View className="flex-row items-center justify-end gap-3">
    <Text variant="title" accessibilityRole="header" className="flex-1">{title}</Text>
    …
<ScrollView horizontal … contentContainerClassName="gap-2"> …
<Sheet …><View className="flex-row flex-wrap gap-2"> …   // ×3 (format/platform/sort)
// Root:
<SafeAreaView edges={['left', 'right']} className="flex-1 bg-ground">
  <FlatList … contentContainerClassName="p-4 gap-4" columnWrapperClassName="gap-3" … />
  <View className="p-4"><UndoSnackbar /></View>
// ItemTile (tileWidth is computed → cannot be a class; stays inline):
<Animated.View style={{ width: tileWidth }}>
  <Pressable … className="gap-1">
```

Effort read: a rewrite, not a move. Every site is re-expressed in a second
vocabulary; computed values (`tileWidth`, and elsewhere `depth * spacing.xl`,
`minHeight`, pressed opacity) stay in `style` forever, so migrated files run a
permanent dual system. The type scale (7 variants × size/line-height/weight/
family) becomes per-variant class strings or codegen'd `@utility` rules —
either way, magic strings detached from `tokens.json`. And `text-[28px]`-style
arbitrary values plus palette classes (`bg-red-500`) sail past `no-raw-colors`
(it matches literals shaped like colors, not class strings), so token
discipline loses its only enforcement without new tooling.

## 7. Comparison

| Axis | Unistyles 3 | Uniwind (free) |
|---|---|---|
| Migration effort / screen | Mechanical move to module sheets; dynamic values become functions; reviewers diff 1:1 | Rewrite into classes; computed values stay in `style` permanently (dual system); type scale becomes magic strings or codegen'd utilities |
| Token enforcement | Themes ARE the TS theme objects; `tokens.json` stays the only source; `no-raw-colors` keeps working | Tokens hand-copied to CSS (or a new codegen step); arbitrary values + palette classes bypass `no-raw-colors`; no enforcement without new tooling |
| `cm-design` ratchet | Unchanged: migrated files drop `no-inline-styles` counts via the documented `--update-baseline` | `className` is invisible to all rules; ratchet needs redesign |
| Jest | Official mocks (`setupFiles`), babel no-op in test; documented limits (first theme, variants stripped); zero style-asserting tests to rewrite | No documented jest story (zero testing mentions in docs); unparsed class strings in trees; `tsc` needs Metro-generated types |
| E2E (must stay pixel-identical) | Same style values through the same props; visual snapshots + computed-color specs are the proof gate | Same values, new engine path; same proof gate — but class→style resolution is the riskier delta |
| Web (static export) | Documented: auto-generated CSS vars/classes + `+html.tsx` import; CSP-safe | CSS-native; CSP-safe |
| iPhone path | Prebuild autolinks (already the flow); nitro present; New Arch always-on | No native code at all |
| Cost | MIT, full C++ engine free | Free tier free but capped; C++ engine/zero re-renders/group variants are Pro ($99/seat/yr, forbidden) |
| Deps added | 1 (`react-native-unistyles`) | 3–4 (`uniwind`, `tailwindcss`, `tailwind-merge`, maybe `tailwind-variants`) |
| Vendor speed claims | Unverified marketing (per the brief, judged by prototype, not benchmarks) | Same — and the benchmarked engine is the paid one |

## 8. Runtime behavior (not measured — verification specified)

No runtime comparison was possible: no device here, and neither package is
installed. The brief says to judge by prototype, and on every prototype-
judgeable axis Unistyles wins (§7). Vendor speed claims from both sides are
taken as unverified marketing. After the install, the migration PR must verify,
on web (this machine) at minimum:

1. `expo export --platform web` builds clean with the new entry/config.
2. `playwright/visual.spec.ts` passes **without** `--update-snapshots`
   (pixel-identical), and `mobile.spec.ts` computed-color assertions pass.
3. Full `npm run check` green (typecheck, lint, words, design ratchet with an
   updated baseline, jest, jest:tz).
4. A TestFlight smoke (owner/lead): cold start, theme follows system
   light/dark, Library grid scrolls without visible jank. No stopwatch claims —
   "feels the same" is the bar.

## 9. `@expo/ui`: not adopted

The issue says to adopt it where it replaces hand-rolled form controls if the
spike shows fit. It doesn't: (a) its web support is experimental while web is
first-class here — universal components would render the least-mature target
where the most users are; (b) there is almost nothing to replace — the app has
one `Modal` (reauth prompt) and zero switches, pickers, sliders, or date
pickers; the `Sheet`/`Chip`/`TextField` controls already encode the custom
design system and accessibility behavior, and a native bottom sheet would change
the design, not preserve it; (c) it is another new dependency for no proven
need. Revisit if a native date picker / menu need arises.

## 10. Decision (ADR-style entry — lead: move to `docs/12-decision-log.md`)

> **ADR-0xx: Unistyles 3 as the app styling layer (#305).** The app migrates
> from hand-rolled StyleSheet to `react-native-unistyles` 3.x, primitives
> first, then tab screens, then pushed screens as they are touched; un-migrated
> screens keep working throughout. Chosen because themes are plain TS objects
> built from `tokens.json` (no second source of truth), the `cm-design` ratchet
> keeps enforcing unchanged, migration is a mechanical per-file move, and jest
> has official mocks. Rejected: **Uniwind** — token duplication into CSS,
> class strings invisible to the design ratchet, no documented jest story, and
> its C++ engine is a paid tier the zero-cost rule forbids; **component kits**
> — per the issue's prior research; **`@expo/ui`** — experimental web, nothing
> to replace. Costs: one dependency, one hot one-liner (`package.json` `main`
> → `index.ts`), and setup files (`babel.config.js`, `index.ts`,
> `src/theme/unistyles.ts`, `src/app/+html.tsx`, jest `setupFiles`).
> Verification: pixel-identical Playwright visual snapshots + a TestFlight
> smoke. Full spike: `app/docs/styling-spike.md`.

## 11. Migration plan (runs after the lead installs)

Setup commit (green on its own): `babel.config.js`, `index.ts`,
`src/theme/unistyles.ts`, `src/app/+html.tsx`, jest `setupFiles`. No screen
changes; full gate green proves the wiring.

Then primitives first, one commit each or small groups (mechanical transform:
inline object → `styles.x`, `colors.` → `theme.colors.`,
`spacing|radius` → `theme.` inside sheets, dynamic values → functions, tree
untouched):

- `src/ui/`: Text, Button, Chip, Card, Screen, ListRow, Sheet, TextField,
  Cover, EmptyState, FormatBadge, LoadingState, Snackbar, LottieSpot
  (`Placeholder` has no styles — nothing to do).
- Tab chrome: `src/app/(tabs)/_layout.tsx`, `src/app/(tabs)/_layout.web.tsx`,
  `src/components/Sidebar.tsx`, `src/components/ScanPill.tsx`
  (`NativeTabBar`, `RootFrame`, `TabsFrame` have no styles — verify, skip).
- Tab screens: `LibraryGrid` + `AddItemPanel` (native), `LibraryView.web` +
  `src/features/library-table/*.web.tsx` (the web table is the Library tab on
  desktop web — 36 sites, the biggest single screen), `ScanTab`, `MeScreen`
  (composes primitives; likely zero direct changes).
- Pushed proof: `src/features/item/ItemDetailScreen.tsx` (the Library tab's
  detail screen; `UndoSnackbar` it renders has no direct styles).

Total: 115 style sites across ~30 files (counted by grep). Finish with
`node scripts/design-gate.mjs --update-baseline` (the documented process —
never hand-edit) and the §8 verification. Un-migrated screens keep working:
Unistyles coexists with plain StyleSheet file by file.

## 12. Requests for the lead

1. **Install**: `npm install react-native-unistyles@3.6.0` on this branch
   (lead owns lockfiles). Peers already satisfied except
   `react-native-edge-to-edge`, which is Android-only and unreferenced by the
   shipped JS — leave it out; the peer warning is harmless.
2. **Hot one-liner**: `package.json` `"main": "expo-router/entry"` →
   `"index.ts"` (required by the Expo Router setup; §4 step 2).
3. **Docs**: move the §10 entry into `docs/12-decision-log.md` (`docs/` is hot).
4. After 1–2, hand the branch back: setup commit → migrate (§11) → §8 proof
   (gates + web screenshots + pixel-identical e2e).

## 13. Sources

- Uniwind docs (1.13): quickstart, theming (basics, style-based-on-themes,
  global-css), tailwind-basics, FAQ, FlatList/Pressable component pages,
  pricing page (free-vs-Pro table).
- Unistyles docs (3.6): getting-started, configuration, Expo Router guide,
  testing, Metro integration, "when to use" page; `react-native-unistyles@3.6.0`
  tarball inspected for `edge-to-edge` JS references (none) and entry points
  (`./plugin`, `./mocks`, `./metro`).
- In-repo: `src/theme/tokens.{json,ts}`, `src/ui/*`, tab routes + `ScanTab` /
  `MeScreen` / `LibraryGrid`, `src/features/item/ItemDetailScreen.tsx`,
  `eslint-rules/cm-design.js`, `scripts/design-gate.mjs`,
  `playwright/{visual,mobile}.spec.ts`, `playwright/serve.mjs` (CSP).
- Versions pinned via `npm view` on 2026-10-25: `uniwind@1.13.0`,
  `tailwindcss@4.3.3`, `react-native-unistyles@3.6.0` (`latest`).


