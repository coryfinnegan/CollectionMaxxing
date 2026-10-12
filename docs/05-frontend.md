# 05 — Front end

## Decision: Expo (React Native) + TypeScript, one codebase for iOS and web

**Why this beats the alternatives for this project** (the full ADR is in [12](12-decision-log.md#adr-003-expo-react-native-for-ios-and-web)):

1. **You're on Windows.** Native iOS builds normally need a Mac with Xcode, and that includes .NET MAUI's iOS builds. **EAS Build compiles iOS in the cloud.** You develop on Windows, install development builds on your iPhone, and ship to TestFlight without owning a Mac.
2. **Scanning quality.** `react-native-vision-camera` v5 with the ML Kit barcode scanner returns every barcode in each frame at 30 fps. It is the most mature multi-barcode option of any cross-platform stack.
3. **One codebase for iOS and web.** Expo Router targets both. Platform-specific pieces (the camera, a dense web table) live in `.ios.tsx` / `.web.tsx` files.
4. **Fast iteration.** Hot reload, OTA updates for JS-only fixes (EAS Update), huge ecosystem.

What you give up: the front end isn't C#. The contract between C# and TypeScript is enforced by the generated client and CI, so the API stays the single source of truth.

## Stack

| Concern | Choice |
|---|---|
| Framework | Expo SDK 57 (React Native 0.86, React 19.2, New Architecture) |
| Language | TypeScript, `strict: true` |
| Navigation | Expo Router (file-based; universal links on iOS, real URLs on web) |
| Server state | TanStack Query v5 (+ persisted cache for offline browsing) |
| Client state | Zustand (scan tray, UI prefs), persisted with `react-native-mmkv` |
| API client | **Generated** by orval from OpenAPI: typed fetchers + TanStack Query hooks + zod schemas |
| Styling | NativeWind (Tailwind classes compiled for RN + web) driven by a design-token file |
| Camera / barcodes | `react-native-vision-camera` v5 + `react-native-vision-camera-barcode-scanner` (iOS); `BarcodeDetector` + zxing-wasm polyfill (web) |
| Images | `expo-image` (disk cache, blurhash placeholders); `expo-image-manipulator` (resize/JPEG before upload); `expo-image-picker` (import shelf photos from the library) |
| Auth | `expo-auth-session` (OIDC code + PKCE), `expo-secure-store` (Keychain); `expo-apple-authentication` in v1.1 |
| Feedback | `expo-haptics`, `expo-audio` (scan beep), `expo-keep-awake` while scanning |
| SSE | `react-native-sse` (iOS); native `EventSource` (web) |
| Lists | FlashList for the library grid (thousands of covers stay at 60 fps) |
| Forms | react-hook-form + zod (shared schemas from the generated client) |
| i18n | English only in v1, but strings are centralised from day one |

## App structure

```
app/
  app/                         # Expo Router routes
    (auth)/sign-in.tsx
    (tabs)/_layout.tsx         # Library | Scan | Me
    (tabs)/library/index.tsx
    (tabs)/library/[itemId].tsx
    (tabs)/scan/index.tsx      # the one scanner: barcodes + auto-snap + receipt
    (tabs)/scan/review/[sessionId].tsx
    (tabs)/scan/photo/[photoId].tsx    # shelf photo results (pins), opened from the receipt
    (tabs)/me/index.tsx
    (tabs)/me/locations.tsx
    (tabs)/me/export.tsx
    +not-found.tsx
  src/
    api/                       # GENERATED, do not edit
    features/
      library/                 # components, hooks, filters
      scan/
        scanner/               # ScannerSource interface, VisionCameraSource (.ios), WebSource (.web), FakeSource (dev/E2E)
        tray/                  # receipt tray: store, de-dupe, offline queue
        review/                # candidate list, pins on photo, bulk apply
      item/
      auth/
    ui/                        # design tokens + primitives (Button, Sheet, Chip, Cover, FormatBadge…)
    lib/                       # http (auth header, refresh, CSRF), sse, haptics, logger
  e2e/
    maestro/*.yaml             # iOS flows
    playwright/*.spec.ts       # web flows
```

### The `ScannerSource` seam

```ts
interface ScannerSource {
  start(onCodes: (codes: DetectedCode[]) => void): Unsubscribe;
  setTorch(on: boolean): void;
  capturePhoto(): Promise<CapturedPhoto>;   // full-res still, taken by auto-snap or the shutter
  onFrameSignals(cb: (s: FrameSignals) => void): Unsubscribe;  // motion, sharpness, text/rectangle presence, perceptual hash
}
```

**Auto-snap** (`features/scan/autosnap/`, pure TypeScript and fully unit-tested):

- A small state machine fed by `FrameSignals`: **idle → candidate** (no barcode, steady ≥ 0.7 s, sharp, text or rectangle present) **→ snap → cooldown** (until the perceptual hash moves far enough from the last snap).
- Any barcode in view resets it to idle.
- Repeated misfires in one session (pictures with nothing found) raise the thresholds automatically.
- On snap: full-res still → `expo-image-manipulator` resize to ≤ 2576 px long edge, JPEG → upload in the background.
- A receipt line with a thumbnail appears immediately (IDENTIFYING…). The camera is ready for the next item within ~300 ms.
- The shutter button triggers the same snap path manually.
- The text boxes from the gate's on-device OCR pass are sent with the picture (`textBoxes`), so the server can draw tight highlights.
- **"Nice scan!"** feedback (flash, check, haptic) fires locally within 100 ms of the shutter or auto-snap, with no network involved.
- **Import from Photos:** `expo-image-picker` (multi-select) → the same resize and upload path with `trigger: import`.
- Result cards are a horizontal FlashList fed by the session's SSE stream; the newest is inserted at the front with a short slide (none with Reduce Motion).

- `VisionCameraSource` (iOS), `WebBarcodeSource` (web), and **`FakeScannerSource`**. The fake one is selectable in dev builds and is what Maestro uses in E2E. A dev menu injects "scan these 6 codes", so scan flows are tested end-to-end without a physical camera.
- The tray logic (stability filter, de-dupe, queueing) is pure TypeScript with unit tests. It knows nothing about cameras.

## Web specifics

- Built with `expo export --platform web` (single-page output) and served by the ASP.NET Core API from `wwwroot`. The API falls back to `index.html` for non-API routes.
- **Same origin**, so the session cookie authenticates API calls. The client adds `X-CSRF: 1` to every mutating request.
- Web-only screens: a **table view** of the library (sortable columns, multi-select, bulk edit) and **drag-and-drop photo scanning** (several photos at once).
- Responsive: at phone widths the web app renders the same layouts as iOS.

## Offline behaviour (v1 scope)

| Capability | Offline? |
|---|---|
| Browse library (last synced) | Yes: TanStack Query persisted cache |
| Scan barcodes into the tray | Yes: decoded on device, queued |
| Resolve / commit | No: queued until online, then automatic |
| Pictures | Capture yes, upload queued (tray lines show QUEUED with the thumbnail) |
| "You own this" for barcodes while offline | v1.1 (owned-identifier set cached on device) |
| Edits to items | No (v1 shows "You're offline" on save) |

## Token handling (iOS)

- The access token is held in memory only. The refresh token lives in the Keychain (`expo-secure-store`, *after-first-unlock, this device only*).
- One shared refresh promise means concurrent `401`s trigger a single refresh. If the refresh fails, the user is sent to sign-in with the tray preserved.
- No third-party API keys are ever shipped in the app bundle. Every provider call goes through our API.

## Builds and releases

| Channel | How | Who |
|---|---|---|
| Dev on iPhone | EAS development build + `expo start` over LAN/tunnel | you, daily |
| TestFlight | `eas build --profile preview` → `eas submit` | on every `v*` tag |
| App Store | `eas build --profile production` → `eas submit` → App Review | milestone M5 |
| JS hotfix | `eas update --channel production` (JS/asset-only changes; native changes need a new build) | as needed |
| Web | Built in CI and baked into the API container image | every deploy |

## Accessibility

- Dynamic Type support; no fixed-height text containers.
- VoiceOver: every scan event is announced ("Added Super Mario 64, Nintendo 64. 6 items in tray."). Icon buttons have labels.
- Touch targets ≥ 44 pt. Colour is never the only signal: matched, ambiguous and not-found also differ in icon and wording.
- Reduced Motion respected (no tray animations).
