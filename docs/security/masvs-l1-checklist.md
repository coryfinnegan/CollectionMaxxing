# OWASP MASVS-L1: check of the iPhone app

Made by QA-1c (issue #169), October 2026, for the Expo app in `app/`. A short check, not a penetration test: each row is a MASVS-L1 area, with Pass (and the test or `file:line`), Accepted risk, or Gap. Paths are under `app/` unless noted. Paths marked "(device)" cannot be proven by a unit test and are on the owner's device checklist.

The app is installed through TestFlight, internal testers only (docs/08 "iOS app", ADR-024), talks only to the owner's server over HTTPS on the tailnet, and does recognition on the phone. Nothing is sent to a third party.

## Result

| # | Severity | Finding | Status |
|---|---|---|---|
| M1 | Low | The refresh token was saved in the Keychain with expo-secure-store's default accessibility (`WHEN_UNLOCKED`), which can travel in a device backup. docs/08 says this-device-only. | **Fixed in this PR**, test first: `src/lib/auth/tokenStore.test.ts` "secure store asks the keychain for this-device-only access"; wired in `src/features/auth/bootstrap.ts`. |
| M2 | Info | The artwork download cannot refuse a redirect (`expo-file-system`). | **Accepted**, see below. |
| M3 | Low | The Face ID app lock and the optional blur over the app switcher (docs/08) are not built: no `LocalAuthentication` use in `src`. Someone holding the owner's unlocked phone can open the app. | Gap, for the lead. L1 does not require either (they are L2 or docs/08 extras). |
| M4 | Info | `normalizeServerUrl` accepts `http://` (`src/features/auth/pairing.ts:38`), for local testing. On a real device App Transport Security refuses plain HTTP to a non-local host, so it cannot be used to send the pairing code in clear. | Accepted: ATS is the guard, and the server address comes from the QR code the owner scans. |

## STORAGE

| Requirement | Status | Evidence |
|---|---|---|
| Sensitive data only in the Keychain (MASVS-STORAGE-1) | Pass (after M1) | The only secret kept on the phone is the refresh token, `cm.credentials` in `src/lib/auth/tokenStore.ts` (`SecureTokenStore`), wired to expo-secure-store in `src/features/auth/bootstrap.ts` with `WHEN_UNLOCKED_THIS_DEVICE_ONLY`. Access tokens are kept in memory only (`src/lib/auth/session.ts`). The web build stores nothing (the cookie is `HttpOnly`). |
| Nothing sensitive in MMKV, AsyncStorage or the logs | Pass | MMKV holds the snap tray and the picture register only (`src/features/scan/pipeline/pictureFiles.ts`). The logger takes plain messages and its tests prove no pairing code or token is passed: `src/features/auth/PairScreen.test.tsx` "never logs the pairing code or token". Server-side: `Hardening/LogRedactionTests`. |
| Temporary pictures are deleted | Pass | Every picture file is registered when made and deleted when used, or at discard, sign-out and unpair; the register is saved so a killed app's files are removed next time: `src/features/scan/pipeline/pictureFiles.ts`, tests in `src/features/scan/pipeline/pictureFiles.test.tsx` ("deletes the camera capture, the scaled snap, both halves and every crop after copying them into the store"). Only temp and cache folders are ever deleted (`isTemp`). Nothing is written to the camera roll. |
| No sensitive data in backups or screenshots | Partly | Keychain item is this-device-only (M1). The app-switcher blur is not built (M3). |

## CRYPTO

| Requirement | Status | Evidence |
|---|---|---|
| No hard-coded keys, no home-made crypto | Pass | The app only uses the system passkey APIs and the Keychain. Search of `src` finds no key material; `process.env` is read only for `EXPO_PUBLIC_API_MOCK` and `EXPO_PUBLIC_API_URL`, neither of which is a secret (`src/app/_layout.tsx:14`, `src/features/auth/services.ts:7`). |

## AUTH

| Requirement | Status | Evidence |
|---|---|---|
| Pairing is verified by the server, not the phone | Pass | The phone sends the one-time token and 6-digit code to `/connect/token`; the server rate-limits wrong codes and locks the guesser: `Pairing/PairingLockoutTests`, `Pairing/PairingRedemptionTests` (server project). The phone maps 400/401/404 to one "code" message (`src/features/auth/pairing.ts:88`) so it does not tell an attacker which part was wrong. |
| Tokens are short-lived and rotating | Pass | `Security/RefreshTokenAbuseTests`, `Pairing/RefreshRotationTests` (server project). |
| Sign-out and unpair clear the Keychain and the pictures | Pass | `SecureTokenStore.clear()` (`src/lib/auth/tokenStore.ts`), `tokenStore.test.ts` "secure store keeps credentials ... and clears"; pictures: `pictureFiles.ts` (see STORAGE). |

## NETWORK

| Requirement | Status | Evidence |
|---|---|---|
| ATS stays at its defaults | Pass | `app.config.ts` sets only `NSCameraUsageDescription` in `infoPlist` (line 17 to 19): no `NSAppTransportSecurity`, no `NSAllowsArbitraryLoads`. |
| TLS to the server; no certificate pinning (by design) | Pass | docs/08: no pinning, because the operational risk outweighs the benefit for this threat model. |
| All third-party calls go through the owner's server | Pass | The phone talks only to the paired server; catalogue lookups are server side. The About screen opens credit links in the browser (`src/features/about/AboutScreen.tsx:34`) and sends no data. |
| The artwork download follows redirects | **Accepted risk** | `expo-file-system`'s download cannot refuse a redirect. Two guards: the server never redirects media (`Security/NoRedirectTests`, locked; QA-1a "Accepted risks" 1), and the phone only accepts normalised `/media/covers/{guid}` paths on its own server (`ownArtworkUrl`). A mistake on one side is caught by the other. |

## PLATFORM

| Requirement | Status | Evidence |
|---|---|---|
| Only the permissions it needs, with a purpose string | Pass | Camera only, with `NSCameraUsageDescription` (`app.config.ts:18`). No location, contacts or tracking; privacy manifest `privacyManifest.json` and its test `privacyManifest.test.ts`. |
| Deep links are validated | Pass | The URL scheme `collectionmaxxing` is registered (`app.config.ts:7`). `collectionmaxxing://pair` opens the pairing screen (`src/app/pair.tsx`), which reads no URL parameters: the pairing token comes from the QR code only, and `parsePairingPayload` (`src/features/auth/pairing.ts:46`) accepts only JSON with a string `serverUrl` and a non-empty string `token`, with the address normalised, and rejects everything else as a "qr" error. The `join/[token]` route reads one path segment and sends it to the server, which answers the same 404 for every unusable invite (`Invites/InviteRulesTests`). |
| No sensitive data in the clipboard, pasteboard or UI the system can read | Pass | The pairing code is typed into a field, never copied by the app (`src/features/auth/PairScreen.tsx`). (device) |
| Web views | N/A | The app has no `WebView`; credit links open in the system browser. |

## CODE

| Requirement | Status | Evidence |
|---|---|---|
| No secrets in the bundle | Pass | Only the two `EXPO_PUBLIC_*` flags above; the generated API client contains routes, not keys. `scripts/security/Run-ZapBaseline.ps1 -WebDist app/dist` scans the exported web bundle (no secret-in-JS findings). |
| Dependencies are checked | Pass, with a note | `scripts/security/Run-DependencyAudit.ps1`: no CRITICAL; two HIGH advisories (`braces`, `node-forge`) in Expo, Metro and Jest build tooling, accepted in `scripts/security/accepted-advisories.txt` with the reason, and a moderate one in `query-string` (`decode-uri-component`, used by expo-router for query parsing: a denial of service by a malformed link, which at worst makes one app launch slow). All are fixed by the next Expo SDK upgrade. |
| Debug features off in release | Pass | The mock mode is chosen by `EXPO_PUBLIC_API_MOCK`, which a release build does not set; the Evals screen is owner-only and the server answers 404 until a golden folder exists (`Evals__Enabled`). |
| Free of known-bad patterns (lint, types) | Pass | `npm run check` in `scripts/check.ps1` (lint with zero warnings, typecheck, tests). |

## What this check did not cover

- **Device behaviour** (Keychain class on a real device, ATS behaviour, the system share of the camera permission). These need a device and are on the owner's iOS checklist (`docs/accessibility` and the OPS-2 notes).
- **MASVS-RESILIENCE** (jailbreak detection, obfuscation): L2 and R only, not applicable to the L1 target.
- **The on-device model path**: recognition runs on the phone (docs/08 "On-device model risks"). A scan picture reaches the server in three cases (a close call's photo, compared and discarded; a kept row's crop; the "Use my photo" cover), each rewritten without its metadata by `JpegSanitizer` (`Scanning/Photo/PhotoAcceptanceTests`).
