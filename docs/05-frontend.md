# 05 — Front end

## Decision: Expo (React Native) + TypeScript, one codebase for iOS and web

**Why this beats the alternatives for this project** (the full ADR is in [12](12-decision-log.md#adr-003-expo-react-native-for-ios-and-web)):

1. **You're on Windows.** Native iOS builds need Xcode on a Mac, whatever the framework. That includes .NET MAUI: its one Mac-free shortcut, Hot Restart, needs the paid membership and is gone from Visual Studio 2026. **GitHub's free macOS runners compile and sign the iOS app in the cloud** and upload it to TestFlight, which installs and updates it on your iPhone (ADR-024; it was AltStore with a free Apple ID until the Apple Developer Program was paid for). No Mac.
2. **Scanning quality.** `react-native-vision-camera` v5 gives full control of the iPhone camera, and a small Swift module plugs Apple's Vision and Foundation Models straight into its frames and stills.
3. **One codebase for iOS and web.** Expo Router targets both. Platform-specific pieces (the camera, a dense web table) live in `.ios.tsx` / `.web.tsx` files.
4. **Fast iteration without spending build minutes.** Most changes are JavaScript, which reloads live on your iPhone from the dev server on your PC (over Tailscale). The cloud Mac is needed only when native code or dependencies change. With SwiftUI, Flutter or MAUI, every change would be a ~20-minute cloud build.

What you give up: the front end isn't C#. The contract between C# and TypeScript is enforced by the generated client and CI, so the API stays the single source of truth.

## Stack

| Concern | Choice |
|---|---|
| Framework | Expo SDK 57 (React Native 0.86, React 19.2, New Architecture) |
| Language | TypeScript, `strict: true` |
| Navigation | Expo Router (file-based; universal links on iOS, real URLs on web) |
| Server state | TanStack Query v5 (+ persisted cache for offline browsing) |
| Client state | Zustand (scan tray, UI prefs), persisted with `react-native-mmkv` |
| API client | **Generated** by orval from OpenAPI: typed fetchers + TanStack Query hooks |
| Styling | NativeWind (Tailwind classes compiled for RN + web) driven by a design-token file |
| Camera / barcodes | `react-native-vision-camera` v5 (iOS) + a frame-processor plugin in `cm-vision` running Apple Vision `VNDetectBarcodesRequest`. **Web: none**; the web app has no camera features. |
| On-device recognition | **`modules/cm-vision`**, a small Expo module in Swift and the only recognition code in the app. It wraps Apple **Vision** (`detectBarcodes` frame plugin, `recognizeText` at 0/90/270°, `detectRectangles`, `featurePrint` + `distance`) and Apple **Foundation Models**: `readItems(image)` (on-device, guided generation, streamed). |
| Your items' feature prints | Stored on the phone (SQLite via `expo-sqlite`) and backed up to the API; compared with `cm-vision.distance` |
| Images | `expo-image` (disk cache, blurhash placeholders); `expo-image-manipulator` (resize/JPEG before upload); `expo-image-picker` (import shelf photos from the library) |
| Auth | QR pairing (scanned with the same Vision barcode plugin) → token endpoint; `expo-secure-store` (Keychain); `expo-local-authentication` for the Face ID app lock |
| Feedback | `expo-haptics`, `expo-audio` (scan beep), `expo-keep-awake` while scanning |
| SSE | `react-native-sse` (iOS); native `EventSource` (web) |
| Lists | FlashList for the library grid (thousands of covers stay at 60 fps) |
| Forms | Controlled inputs (`useState` + the shared `TextField`), validated inline where they're used (no form library) |
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
        scanner/               # ScannerSource interface, VisionCameraSource (iOS), FakeSource (dev/E2E); no web scanner
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
- The gate's Apple Vision pass is reused: its text lines and boxes feed item splitting (spine columns, rectangles) directly, so nothing is computed twice. Each item's crop, feature print and box stay on the phone; only text goes to `/identify`.
- **"Nice scan!"** feedback (flash, check, haptic) fires locally within 100 ms of the shutter or auto-snap, with no network involved.
- **Import from Photos:** `expo-image-picker` (multi-select) → the same on-device pipeline (`trigger: import`).
- Result cards are a horizontal FlashList fed by the session's SSE stream; the newest is inserted at the front with a short slide (none with Reduce Motion).

- `VisionCameraSource` (iOS) and **`FakeScannerSource`**. There's no web scanner. The fake one is selectable in dev builds and is what Maestro uses in E2E. A dev menu injects "scan these 6 codes", so scan flows are tested end-to-end without a physical camera.
- The tray logic (stability filter, de-dupe, queueing) is pure TypeScript with unit tests. It knows nothing about cameras.

## Recognition engines

```ts
interface RecognitionEngine {
  readonly name: 'apple_model' | 'vision_pipeline';
  isAvailable(): Promise<boolean>;                       // Apple model: iOS 27 + Apple Intelligence + image support
  read(still: Still, onItem: (item: ReadItem) => void): Promise<SnapReading>;  // streams items as they're read
}
```

- `AppleModelEngine` wraps `cm-vision.readItems`. `VisionPipelineEngine` wraps OCR + rectangles + the TypeScript splitting logic.
- An `EngineSelector` picks one when the scanner opens. Per snap, it falls back to the Vision text engine if the model declines, times out (4 s) or runs out of context.
- Both feed the same feature-print step and the same `/identify` call, so the server, the scorer and the UI don't care which engine read the picture.

## Native code on a Windows machine

The on-device recognition needs one small native module in Swift (`modules/cm-vision`). You can't compile Swift on Windows. **GitHub's macOS runner compiles it in the cloud** for every build, so the plan keeps that module deliberately thin:

- **A handful of functions, no logic:** recognise text, detect rectangles, feature print, and (iOS 27) `readItems`, which returns the model's structured items as JSON. The `@Generable` schema lives in Swift, mirrored by a TS type checked by a contract test. Callstack's `@react-native-ai/apple` doesn't support image input yet; if it adds it, we can swap to it. Everything clever (spine grouping, item splitting, scoring inputs) lives in TypeScript and is unit-tested on Windows.
- **Behind a TS interface** (`VisionEngine`), with a `FakeVisionEngine` that replays fixture JSON captured from a real iPhone, so tests never touch native code (and the web build doesn't include the module at all).

## Web specifics

- Built with `expo export --platform web` (single-page output) and served by the ASP.NET Core API from `wwwroot`. The API falls back to `index.html` for non-API routes.
- **Same origin**, so the session cookie authenticates API calls. The client adds `X-CSRF: 1` to every mutating request.
- Web-only screens: a **table view** of the library (sortable columns, multi-select, bulk edit).
- **No camera, scanning or recognition on the web.** The scanner tab isn't rendered there, and `cm-vision` isn't bundled. Adding items on the web is by typed search or CSV import.
- Responsive: at phone widths the web app renders the same layouts as iOS.

## Offline behaviour (v1 scope)

| Capability | Offline? |
|---|---|
| Browse library (last synced) | Yes: TanStack Query persisted cache |
| Scan barcodes into the tray | Yes: decoded on device, queued |
| Resolve / commit | No: queued until online, then automatic |
| Pictures | Fully processed on the phone; the catalogue match waits as QUEUED until you're online. Your own confirmed items match instantly, offline. |
| "You own this" while offline | Yes: the phone keeps a synced copy of what the library owns (identifiers, titles, feature prints) |
| Edits to items | No (v1 shows "You're offline" on save) |

## Token handling (iOS)

- The access token is held in memory only. The refresh token lives in the Keychain (`expo-secure-store`, *after-first-unlock, this device only*).
- One shared refresh promise means concurrent `401`s trigger a single refresh. If the refresh fails, the user is sent to sign-in with the tray preserved.
- No third-party API keys are ever shipped in the app bundle. Every provider call goes through our API.

## Builds and releases (TestFlight, ADR-024)

| Channel | How | When |
|---|---|---|
| **Build** | `.github/workflows/ios-build.yml` on a GitHub **macOS runner**: `expo prebuild` → `xcodebuild archive`, signed with the Apple Distribution certificate and App Store profile from the `testflight` environment → export → upload. ~25–35 min. The repo is public, so GitHub's standard macOS runners are **free with no minute limit**. | on `v*` tags, or by hand |
| **Install and update** | **TestFlight**, internal testers only: the build reaches the Household group automatically once Apple has processed it, and TestFlight updates the app by itself. | after each build |
| **Stay installed** | A TestFlight build lasts **90 days**. The same workflow rebuilds the latest release on the 1st of every other month, so a quiet spell never lets it lapse. | every other month, automatic |
| **Dev builds** | Not set up. They would need `expo-dev-client`, your iPhone registered as a device, and a development profile. Until then JavaScript changes are tried on the web and then in a TestFlight build. | when native code changes |
| **Web** | Built in CI and baked into the API image on your PC | every deploy |

**One-time setup:**

- the Apple side (app ID, certificate, profile, App Store Connect app, API key) and the GitHub secrets: [iphone-app-testflight.md](../deploy/runbooks/iphone-app-testflight.md), about 30 minutes;
- on the iPhone: install TestFlight and accept the invite, and install Tailscale with VPN On Demand.

There's no Developer Mode, no weekly re-signing and no app limit. The release signing applies only to the Release build of the app target, through a small local config plugin (`app/plugins/withReleaseSigning.js`), and only when CI passes a team and a profile. `ITSAppUsesNonExemptEncryption` is false (HTTPS only), so builds skip the export compliance question.

**Apple capabilities:** the membership would allow push notifications, Sign in with Apple, associated domains and Private Cloud Compute. The plan still uses none of them (ADR-024 says why). Camera, Vision, LocalAuthentication (Face ID) and the on-device Foundation Model need no capability. The M0 spike confirmed them on a free-signed build, and a TestFlight build behaves the same.

**Family members' iPhones:**

- Each person is an App Store Connect user with the Marketing role, limited to this app, and a tester in the Household group. Apple allows up to 100.
- The web app explains this under **Settings → Get the iPhone app**.
- It works anywhere: there's nothing to refresh at home.
- Family who only browse don't need the app: the web app works in Safari, or on Android.
- iPhones without Apple Intelligence use the Vision text engine automatically.

Builds are uploaded as *TestFlight internal testing only*, so they can never reach external testers or the App Store. No EAS Build. The API base URL (your PC's Tailscale HTTPS name) comes from the QR code when you pair.

## Accessibility

- Dynamic Type support; no fixed-height text containers.
- VoiceOver: every scan event is announced ("Added Super Mario 64, Nintendo 64. 6 items in tray."). Icon buttons have labels.
- Touch targets ≥ 44 pt. Colour is never the only signal: matched, ambiguous and not-found also differ in icon and wording.
- Reduced Motion respected (no tray animations).
