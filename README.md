# CollectionMaxxing

Track every book, VHS tape, DVD / Blu-ray and video game you own by pointing your phone at them.

> **Status (2026-10-11):** Full plan written. Waiting for your review. There is no code yet.
> Each doc is a proposal. Items marked **Decision needed** need an answer from you before M0 starts.

## The idea in one paragraph

Open the app, tap **Scan**, and point the camera at your stuff. There are no modes to pick; the scanner works out what it's looking at:

- **Barcodes** are read instantly, as many as are in view.
- **Something without a barcode** (a loose N64 cartridge, a VHS sleeve, a bare disc) gets snapped automatically once you hold it steady. The app identifies it from the label, the cartridge shape and the artwork.
- **A whole shelf of spines** gets snapped the same way, and every title in the picture is read.

Results pop up as cards while you keep scanning. Tap one to see that exact item highlighted on your shelf photo. This is the flow of the Flippr reel you shared, without its category picker.
The server matches everything against book, movie and game databases. It flags anything you already own. You review the batch and tap **Add 14 items**.
The same library is available on the web, where bulk editing in a table is easier.

## The plan at a glance

| Area | Decision | Doc |
|---|---|---|
| Backend | **.NET 10 (LTS)**, ASP.NET Core Minimal APIs, EF Core 10, PostgreSQL 17 | [01](docs/01-architecture.md) |
| Architecture | Modular monolith: Domain / Application / Infrastructure / Api, enforced by architecture tests | [01](docs/01-architecture.md) |
| Front end | **Expo (React Native) + TypeScript**. One codebase for iOS and web, with cloud iOS builds so no Mac is needed | [05](docs/05-frontend.md) |
| Scanning | **One scanner, no modes.** Barcodes are read on the device, many per frame. With no barcode in view, the phone auto-snaps a picture when it's steady on something, and the server works out whether it's one item (a cart, a sleeve, a box) or a whole shelf. Every result says up front if you already own it. | [02](docs/02-scanning-and-recognition.md) |
| Recognition | **iPhone only, Apple frameworks only, $0 per scan.** Vision reads barcodes and text. **Apple's on-device Foundation Model (iOS 27) reads each picture**: items, titles, platform, edition. Vision feature prints compare artwork on the phone. The server grounds everything against the catalogue from **text only**, and photos stay on the phone. Items it can't resolve go to a quick Fix it search. No third-party AI, no models to host. **The web app does no recognition.** | [02](docs/02-scanning-and-recognition.md) |
| Metadata | Open Library, Google Books, TMDB, IGDB, UPCitemdb, all behind one provider interface with caching | [02](docs/02-scanning-and-recognition.md) |
| Auth | Passwordless: a one-time setup code creates the owner; **passkeys** on the web; the iPhone is **paired by QR code**, with an optional Face ID app lock. OpenIddict + ASP.NET Core Identity. Reachable only over Tailscale. | [07](docs/07-authentication-and-authorization.md) |
| Authorization | Deny by default. Resource-based library roles (Owner / Editor / Viewer) and an automated authorization-matrix test | [07](docs/07-authentication-and-authorization.md) |
| Security | OWASP API Top 10 mapped to controls, threat model, EXIF stripping, LLM prompt-injection containment | [08](docs/08-security-and-privacy.md) |
| Testing | Strict TDD. xUnit v3, Testcontainers (real Postgres), WireMock, Verify, Maestro, Playwright, AI eval suite | [09](docs/09-testing-and-tdd.md) |
| Hosting | **Self-hosted on your gaming PC**: Docker Compose (API + Postgres + backups), reached privately from your iPhone anywhere through **Tailscale** (already installed; no open ports). GitHub Actions builds (free, since the repo is public); a deployer on the PC pulls and rolls out new versions. | [10](docs/10-hosting-and-devops.md) |
| Cost | **$0, full stop** (the PC is already on 24/7 for Plex and Jellyfin): self-hosted, free Apple ID with sideloading (no $99 membership), free tiers everywhere else | [10](docs/10-hosting-and-devops.md) |
| Delivery | Six milestones, built by **parallel agents in five waves**: a serial foundation, then 4–6 agents at a time, each in its own worktree, with a verifier agent on every PR. About **2–3 weeks** to v1, with about an hour a day from you. | [11](docs/11-roadmap.md), [13](docs/13-agent-orchestration.md) |

**Wireframes and mockups:** a design canvas with low-fi wireframes and high-fi mockups for the key iOS screens and the web library.
Link: <https://claude.ai/artifact/FBBMc4KUa33nCXaHLpCZhe>. The ASCII wireframes with interaction notes are in [docs/06](docs/06-ux-and-wireframes.md).

## Read in this order

1. [Product: vision, scope, releases](docs/00-product.md)
2. [Architecture](docs/01-architecture.md)
3. [Scanning and recognition](docs/02-scanning-and-recognition.md). This is the core of the app.
4. [Data model](docs/03-data-model.md)
5. [API design](docs/04-api.md)
6. [Front end](docs/05-frontend.md)
7. [UX and wireframes](docs/06-ux-and-wireframes.md)
8. [Authentication and authorization](docs/07-authentication-and-authorization.md)
9. [Security and privacy](docs/08-security-and-privacy.md)
10. [Testing and TDD](docs/09-testing-and-tdd.md)
11. [Hosting and DevOps](docs/10-hosting-and-devops.md)
12. [Roadmap](docs/11-roadmap.md)
13. [Decision log (ADRs)](docs/12-decision-log.md)
14. [Building it with parallel agents](docs/13-agent-orchestration.md)

## Decision needed

These are the calls I could not make for you. Each one has a recommended default, so "yes to all defaults" is a valid answer.

| # | Question | Default if you say nothing |
|---|---|---|
| D1 | ~~Expo / React Native?~~ **Decided 2026-10-12: Expo**, because it builds iOS with no Mac of your own and no paid membership, and everyday changes reload on your iPhone without a cloud build. See [ADR-003](docs/12-decision-log.md#adr-003-expo-react-native-for-ios-and-web). | Resolved |
| D2 | ~~Own the identity server or use Entra External ID?~~ **Resolved by self-hosting:** OpenIddict inside the API. Setup code → passkeys on the web; the iPhone is paired by QR code. | Resolved |
| D3 | ~~Azure budget~~ **Resolved 2026-10-12: self-hosted on your PC**, $0 hosting. | Resolved |
| D4 | ~~Cloud AI for hard cases?~~ **Decided 2026-10-12: no third-party AI.** Apple's on-device frameworks only. | Resolved |
| D5 | ~~Apple Developer Program ($99/yr)?~~ **Decided 2026-10-12: no.** The app is built on GitHub's free macOS runners and sideloaded with your free Apple ID through AltStore (auto re-signed weekly by your PC). | Resolved |
| D6 | ~~Household sharing in v1 or later?~~ **Decided 2026-10-12: v1.** Family members join your library by invite, as Editor or Viewer. See [ADR-020](docs/12-decision-log.md#adr-020-household-sharing-in-v1). | Resolved |
| D7 | ~~Collection value (market price per item)?~~ **Decided 2026-10-12: no pricing of any kind**, neither market value nor price paid. The app is purely a collection tracker. | Resolved |
| D8 | Roughly **how many items**, and do you have **existing spreadsheets/lists** to import? This affects the import priority and API quotas. | ~2,000, CSV import in v1.1 |
| D9 | **Music** (CDs, vinyl, cassettes) or **LaserDisc**, in scope or not? They are easy to add later through Discogs. | Out of v1 |
| D10 | ~~Domain name~~ **Not needed:** Tailscale gives the PC an HTTPS name (`<pc>.<tailnet>.ts.net`). | Resolved |
| D12 | ~~Which iPhone do you scan with?~~ **Decided 2026-10-12: iPhone 17 Pro.** It qualifies, so Apple's on-device model is the expected primary reader, confirmed by the M3 gate on that phone. | Resolved |
| D13 | ~~Keep the PC awake 24/7?~~ **Decided 2026-10-12: yes.** It's already on around the clock for Plex, ErsatzTV and Jellyfin, so it costs nothing extra. | Resolved |
| D11 | ~~Artwork index platforms~~ **No longer needed.** With Apple frameworks only, there's no artwork index: the phone compares artwork against your own items and the top candidates. | Resolved |
| D14 | ~~Public or private repo?~~ **Decided 2026-10-13: public.** CI and iOS builds are free with no limits. Because anyone can read it:<br/>• no secrets, photos or personal data go in the repo;<br/>• your PC pulls deploys, so nothing outside can push code to it;<br/>• agents only take briefs from you.<br/>See [ADR-022](docs/12-decision-log.md#adr-022-public-repository). | Resolved |
| D15 | ~~How many agents at once?~~ **Decided 2026-10-13: 4.** Model tiers keep token use down: Haiku for mechanical work, Sonnet for everything else (high effort for risky areas). No Opus. See [13](docs/13-agent-orchestration.md#keeping-token-usage-down). | Resolved |
| D16 | How hands-on? **(a)** approve each wave's plan, with a checkpoint after each wave; **(b)** the lead runs waves back to back and stops only at checkpoints | (a) |

## Repository layout (planned)

```
CollectionMaxxing/
├─ README.md                    ← you are here
├─ docs/                        ← this plan
├─ src/                         ← .NET solution (M0)
├─ tests/                       ← .NET test projects (M0)
├─ app/                         ← Expo app: iOS + web (M0)
├─ deploy/                      ← Docker Compose, Tailscale Serve, backups, runbooks (M0)
└─ .github/workflows/           ← CI/CD (M0)
```
