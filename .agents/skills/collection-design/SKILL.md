---
name: collection-design
description: CollectionMaxxing's own design system (Phase 0 tokens, Bricolage/Space Mono type, voice rules, banned patterns). Use for any app UI work: screens, components, copy, or styling in app/src.
---

# CollectionMaxxing Design

You are styling the CollectionMaxxing app. Its design system is already decided;
your job is to follow it, not to invent a new one.

## Read first

Read `DESIGN.md` at the repo root before writing or changing any UI. It names the
token palette, the type scale, the voice rules, and the banned patterns. The
tokens live in `app/src/theme/tokens.json` (single source of truth); the shared
primitives live in `app/src/ui/`.

## Precedence

1. `DESIGN.md` and `app/src/theme/tokens.json` always win.
2. The Expo skills (`expo-native-ui`, `expo-design-system`, `expo-router`) and
   `frontend-design` advise on technique only. Where they suggest something
   DESIGN.md bans (raw hex colors, gradients, doubled titles, British
   spellings), DESIGN.md wins — do not follow the skill.

## Hard rules (the gate enforces these)

- Never write a raw color literal (`#fff`, `rgb(...)`) outside `tokens.json`.
  Use `useColors()` roles and the `fixed` values.
- Never add an inline `style={{...}}` object in a new file; use `StyleSheet`
  or the shared primitives. (Older files are grandfathered; do not copy them.)
- Never put raw text directly inside a `View` or other non-text component;
  use the `Text` primitive.
- One title per screen: the navigator header names phone screens, so in-screen
  titles render on the web only (`Screen` already handles this). Do not add a
  second title that repeats the header.
- US English, sentence case, verb-first buttons with counts ("Add 11 items").
  No exclamation marks, no apologies, no "Oops".

## Libraries

This app does not use Tailwind/NativeWind, `@expo/ui`, or gradients. Do not
add them. Press feedback comes from `usePressFeedback` and haptics from
`app/src/lib/haptics.ts`.

Deliberately not installed from the skill upstreams (#307): the `expo-ui`
skill (steers toward `@expo/ui` and away from Reanimated/react-native-web,
against this stack) and `expo-tailwind-setup` (does not exist upstream;
verified 2026-10-25, and the app uses StyleSheet).
