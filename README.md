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
| AI | Anthropic API, `claude-opus-5-5` vision with structured JSON output, plus a **self-hosted visual index** (an image-embedding model running in .NET + pgvector) that matches artwork and learns from every item you confirm. About $0.03–0.05 per single-item picture, $0 for repeats, $0.05–0.12 per shelf picture. | [02](docs/02-scanning-and-recognition.md) |
| Metadata | Open Library, Google Books, TMDB, IGDB, UPCitemdb, all behind one provider interface with caching | [02](docs/02-scanning-and-recognition.md) |
| Auth | Passwordless: Sign in with Apple, passkeys, Google. OpenIddict (OIDC server) + ASP.NET Core Identity | [07](docs/07-authentication-and-authorization.md) |
| Authorization | Deny by default. Resource-based library roles (Owner / Editor / Viewer) and an automated authorization-matrix test | [07](docs/07-authentication-and-authorization.md) |
| Security | OWASP API Top 10 mapped to controls, threat model, EXIF stripping, LLM prompt-injection containment | [08](docs/08-security-and-privacy.md) |
| Testing | Strict TDD. xUnit v3, Testcontainers (real Postgres), WireMock, Verify, Maestro, Playwright, AI eval suite | [09](docs/09-testing-and-tdd.md) |
| Hosting | Azure Container Apps + Postgres Flexible Server + Blob + Key Vault, managed identity throughout, Bicep, GitHub Actions | [10](docs/10-hosting-and-devops.md) |
| Cost | About $40–55/month, plus Apple's $99/year | [10](docs/10-hosting-and-devops.md) |
| Delivery | Six milestones, M0 (walking skeleton) through M5 (App Store), about 14 part-time weeks | [11](docs/11-roadmap.md) |

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

## Decision needed

These are the calls I could not make for you. Each one has a recommended default, so "yes to all defaults" is a valid answer.

| # | Question | Default if you say nothing |
|---|---|---|
| D1 | OK with **Expo / React Native** for the front end? The alternatives were .NET MAUI + Blazor, and native SwiftUI + a separate web app. See [ADR-003](docs/12-decision-log.md#adr-003-expo-react-native-for-ios-and-web). | Expo |
| D2 | **Own the identity server** (OpenIddict, free, more code) or **Microsoft Entra External ID** (managed, free tier, less control)? | OpenIddict |
| D3 | Is about **$40–55/month on Azure** OK, or do you want the ~$10–15/month budget mode? | Standard |
| D4 | OK to send shelf photos to **Anthropic's API** for identification? Anthropic does not train on API data. Photos are stripped of location data first. | Yes |
| D5 | Do you have an **Apple Developer Program** membership ($99/year)? TestFlight and Sign in with Apple need one. | Buy one in M0 |
| D6 | **Household sharing** (a partner or kids as members of your library) in v1, or later? The data model supports it either way. | v1.1 |
| D7 | ~~Collection value (market price per item)?~~ **Decided 2026-10-12: no pricing of any kind**, neither market value nor price paid. The app is purely a collection tracker. | Resolved |
| D8 | Roughly **how many items**, and do you have **existing spreadsheets/lists** to import? This affects the import priority and API quotas. | ~2,000, CSV import in v1.1 |
| D9 | **Music** (CDs, vinyl, cassettes) or **LaserDisc**, in scope or not? They are easy to add later through Discogs. | Out of v1 |
| D10 | A **domain name** for the API and web app. | Placeholder `[YOUR-DOMAIN]` |
| D11 | Which **game platforms** should the visual index pre-learn, so loose carts and boxes match on the first snap? (Movies are learned as you go.) | NES, SNES, N64, Game Boy family, Genesis, PS1, PS2, GameCube |

## Repository layout (planned)

```
CollectionMaxxing/
├─ README.md                    ← you are here
├─ docs/                        ← this plan
├─ src/                         ← .NET solution (M0)
├─ tests/                       ← .NET test projects (M0)
├─ app/                         ← Expo app: iOS + web (M0)
├─ infra/                       ← Bicep (M0)
└─ .github/workflows/           ← CI/CD (M0)
```
