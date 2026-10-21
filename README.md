# CollectionMaxxing

Track every book, VHS tape, DVD / Blu-ray and video game you own by pointing your phone at them.

Open the app, tap **Scan**, and point the camera at your stuff. There are no modes to pick; the scanner works out what it's looking at:

- **Barcodes** are read instantly, as many as are in view.
- **Something without a barcode** (a loose cartridge, a VHS sleeve, a bare disc) is snapped automatically once you hold it steady and identified from its label and artwork.
- **A whole shelf of spines** is snapped the same way, and every title in the picture is read.

Results pop up as cards while you keep scanning. The server matches them against book, movie and game databases and flags anything you already own. You review the batch and tap **Add 14 items**. The same library is available on the web, where bulk editing is easier.

It is **self-hosted and costs $0**: the API and database run on one Windows PC, you reach them privately over Tailscale, and the iPhone app is sideloaded with a free Apple ID.

## What's in the repo

| Part | Where | What it is |
|---|---|---|
| API | `src/CollectionMaxxing.Api` (+ `Application`, `Domain`, `Infrastructure`) | .NET 10, ASP.NET Core Minimal APIs, EF Core, PostgreSQL 17 |
| Dev host | `src/CollectionMaxxing.AppHost` | .NET Aspire host: starts Postgres and the API for local development |
| App | `app/` | Expo (React Native) + TypeScript: one codebase for the iPhone app and the web app |
| Deploy | `deploy/` | Docker Compose stack, deployer, backups, Tailscale Serve, runbooks |
| Tests | `tests/`, `app/src`, `deploy/tests` | xUnit v3 + Testcontainers, Jest, Playwright, PowerShell |
| Contract | `contracts/openapi.json` | The API contract; the app's client is generated from it and never committed |
| Docs | `docs/` | The full plan: architecture, API, data model, security, ADRs |

## Prerequisites (development, Windows)

- **.NET SDK 10** (`global.json` pins 10.0.400)
- **Node.js 22 LTS** and npm
- **Docker Desktop**, running (the API tests and the local database use containers)
- **PowerShell 7** (`pwsh`)
- Optional: provider API keys (TMDB, IGDB and so on) via `dotnet user-secrets`; fakes are used when a key is missing

## Run it locally

### API and database

```powershell
dotnet run --project src/CollectionMaxxing.AppHost
```

This starts Postgres 17 in a container (data kept in a named volume), applies migrations, and serves the API on **http://localhost:5480**. The Aspire dashboard URL is printed in the console. Port 5480 is fixed; 8080, 8409, 32400 and 8096 belong to other apps on the PC.

### Web app

```powershell
cd app
npm ci
npm run generate     # builds the API client from contracts/openapi.json
npm run web          # dev server in the browser
```

`npm run build:web` produces the static export (`expo export --platform web`) that the release workflow bakes into the API image.

### iPhone app, day to day

The camera and on-device recognition need a real iPhone on iOS 27, so the Simulator is not enough.

1. Install a build once (see *iPhone app* below).
2. On the PC: `cd app && npm run start`. The phone reaches it over Tailscale, so this works away from home too.

## Test

Run the quiet gate before every commit. It restores, builds (warnings are errors), runs the .NET tests and runs the app's checks. Docker must be running.

```powershell
pwsh scripts/check.ps1        # or: bash scripts/check.sh
```

Pieces on their own:

| What | Command |
|---|---|
| All .NET tests | `dotnet test CollectionMaxxing.slnx` |
| App: generate, typecheck, lint, Jest | `cd app && npm run check` |
| App: browser end-to-end | `cd app && npm run test:e2e` |
| Deploy scripts | `pwsh -NoProfile -File deploy/tests/Run-Tests.ps1` (`-SkipDocker` skips the Docker suites) |

## How each piece is built

| Output | Built by | Trigger | Result |
|---|---|---|---|
| API, migrate and backup images, with the web app inside the API image | `.github/workflows/release.yml` (ubuntu) | every push to `main` | Signed, attested images on GHCR, tagged with the commit SHA |
| Unsigned iPhone `.ipa` | `.github/workflows/ios-build.yml` (macOS runner, Xcode 27) | a `v*` tag, or run it by hand | `CollectionMaxxing-unsigned.ipa` and its `.sha256` on a GitHub release (a manual run leaves them as a workflow artifact) |
| Checks | `ci.yml`, `mutation.yml`, `security-nightly.yml` | pull requests and nightly | Must be green before merging |

You never build these by hand: merge to `main` and the images build themselves.

## Install and run in production (the PC)

The PC **pulls**. A small deployer on the PC checks GitHub every 2 minutes for a new image, verifies this repo built it from `main`, runs migrations, swaps it in, and rolls back if the health check fails. Nothing pushes to the PC and no ports are open to the internet.

```
push to main -> GitHub Actions builds + signs images -> GHCR
                                                          | pulled every 2 min by the deployer
iPhone / browser -> Tailscale HTTPS -> tailscale serve -> 127.0.0.1:5480 (API + web app) -> Postgres
```

**One-time install** (about an hour). The full checklist, with what to expect and how to undo each step, is in [deploy/README.md](deploy/README.md). In short:

1. Clone the repo to `C:\CollectionMaxxing\repo`.
2. Copy `deploy/.env.example` to `C:\CollectionMaxxing\.env` and fill it in (database password, your Tailscale name, backup folder).
3. Create the token signing keys: `deploy/deployer/New-Secrets.ps1 -Apply`.
4. Dry-run, then do the first deploy: `deploy/deployer/Deploy-CollectionMaxxing.ps1 -Apply`.
5. Publish it privately: `deploy/tailscale/Set-TailscaleServe.ps1 -Apply`.
6. Register the 2-minute deployer task: `deploy/deployer/Install-Deployer.ps1 -Apply`.
7. Check the first backup, and optionally add a heartbeat email.

Every script is a dry run unless you pass `-Apply`. After this, updates are automatic.

**First sign-in:** the one-time setup code is in `C:\CollectionMaxxing\secrets\setup-code.txt` (and in the API logs). Open `https://<your-pc>.<tailnet>.ts.net` and use it to create the owner passkey. Family members join by invite as Editor or Viewer.

Day-to-day operations (health checks, restore, rollback, what to do when something breaks) are in [deploy/runbooks](deploy/runbooks/README.md) and [deploy/RESTORE.md](deploy/RESTORE.md).

## iPhone app: build, install, update

There is no paid Apple membership. The app is built unsigned on GitHub's macOS runner, then signed and installed with a free Apple ID through AltStore.

**Release a new build**

1. Bump `version` in `app/app.config.ts` and merge to `main`.
2. Tag the merge commit with the same number: `git tag v1.0.3 && git push origin v1.0.3`. The build fails if the tag and `app.config.ts` disagree.
3. `ios-build.yml` attaches the `.ipa` to the GitHub release.

**Install the first time**

1. On the PC, install **AltServer** (it needs Apple's iTunes and iCloud for Windows). On the iPhone, install **AltStore** with your free Apple ID.
2. On the iPhone, on the same Wi-Fi as the PC, download the `.ipa` from the latest release (or from the web app under **Settings → Get the iPhone app**).
3. In AltStore: **My Apps → + →** pick the file.
4. Turn on Tailscale on the phone, open the app and pair it by scanning the QR code from the web app.

**Stay installed and up to date**

- Free signatures last 7 days. AltStore re-signs automatically whenever the phone and PC are on the same Wi-Fi; your data survives.
- The deployer copies each new release's `.ipa` onto the server, and the app shows an "update ready" notice when it is newer than the installed version. Install it through AltStore the same way.

## Docs

The plan lives in [docs/](docs/). Start with these:

1. [Product: vision, scope, releases](docs/00-product.md)
2. [Architecture](docs/01-architecture.md)
3. [Scanning and recognition](docs/02-scanning-and-recognition.md)
4. [Data model](docs/03-data-model.md) and [API design](docs/04-api.md)
5. [Front end](docs/05-frontend.md) and [UX and wireframes](docs/06-ux-and-wireframes.md)
6. [Authentication](docs/07-authentication-and-authorization.md) and [Security and privacy](docs/08-security-and-privacy.md)
7. [Testing and TDD](docs/09-testing-and-tdd.md)
8. [Hosting and DevOps](docs/10-hosting-and-devops.md)
9. [Roadmap](docs/11-roadmap.md), [Decision log](docs/12-decision-log.md) and [Building it with parallel agents](docs/13-agent-orchestration.md)

Contributors and agents: read [CLAUDE.md](CLAUDE.md) first. The repo is public, so never commit secrets, photos or personal data.
