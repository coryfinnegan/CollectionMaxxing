# 10 — Hosting and DevOps (self-hosted)

**Decision (2026-10-12):** CollectionMaxxing is **self-hosted on your gaming PC**. There's no cloud hosting bill, and your data stays at home.

**The machine:**

- Windows 11 Pro
- Intel i9-13900KF (24 cores) and 32 GB RAM
- NVIDIA RTX 4070 Ti (12 GB)
- C: 1.8 TB SSD (326 GB free), F: 1.8 TB (415 GB free), plus D: and E:
- Docker 24.0.7 and Tailscale already installed

The app needs a tiny fraction of that: well under 1 CPU core and ~1 GB RAM at idle. You won't notice it while gaming.

## How your iPhone reaches it: Tailscale

You already have **Tailscale** installed. It puts your PC and your iPhone on a private, encrypted network (WireGuard) that works from anywhere: home Wi-Fi, a thrift store, cellular.

- **Nothing is exposed to the internet.** No port forwarding, no public IP, no router changes.
- **HTTPS for free.** `tailscale serve` gives the PC a real certificate for `https://<pc-name>.<your-tailnet>.ts.net` and forwards it to the API on `localhost`. That one URL serves the API and the web app.
- **Who can reach it:** only devices signed in to your tailnet. Tailscale's free Personal plan covers you and a couple of family members. The app's own sign-in still applies on top.
- **On the iPhone:** install the Tailscale app and turn on *VPN On Demand*, so it connects automatically when the CollectionMaxxing app needs it.

*Later, if you ever want public share links or family without Tailscale:* a Cloudflare Tunnel can publish just the web app on a domain, with no open ports either. Not needed for v1.

## What runs on the PC

```mermaid
flowchart TB
  phone["iPhone (anywhere)<br/>Tailscale app"] -- "WireGuard" --> ts
  browser["Browser on a tailnet device"] -- "WireGuard" --> ts
  subgraph pc["Your PC: Windows 11 Pro"]
    ts["Tailscale Serve<br/>https://pc.tailnet.ts.net → 127.0.0.1:8080"]
    subgraph compose["Docker Compose: collectionmaxxing"]
      api["api<br/>.NET 10: REST API + web app + jobs"]
      mig[["migrate<br/>EF bundle, runs first"]]
      pg[("postgres 17<br/>volume on C:")]
      files[("files volume<br/>covers, your-photo covers, exports")]
      dash["aspire-dashboard<br/>logs · traces · metrics"]
      bak[["backup<br/>nightly"]]
    end
    runner["GitHub Actions<br/>self-hosted runner (deploy only)"]
    backups[("F:\\Backups\\CollectionMaxxing")]
  end
  ts --> api
  api --> pg
  api --> files
  mig --> pg
  api -. OTLP .-> dash
  bak --> pg
  bak --> backups
  backups -. weekly, encrypted .-> offsite["Off-site copy<br/>(OneDrive or Backblaze B2)"]
  api --> providers["Open Library · Google Books ·<br/>TMDB · IGDB · UPCitemdb"]
```

| Service | Image / tool | Notes |
|---|---|---|
| `api` | our .NET 10 image (chiseled, non-root), published to GitHub Container Registry (private) | Listens on `127.0.0.1:8080` only; Tailscale Serve is the only way in. Serves the web app from `wwwroot`. Background jobs (UPC pending queue, exports) run in-process. |
| `migrate` | EF Core migration bundle | A one-shot container; `api` starts only after it succeeds |
| `postgres` | `postgres:17` | Not published to the host at all; only `api`, `migrate` and `backup` can reach it on the compose network. `pg_trgm` is enabled by a migration. |
| `files` | Docker volume | Replaces cloud blob storage (`IBlobStore` → file-system implementation) |
| `aspire-dashboard` | Microsoft's standalone Aspire Dashboard | OpenTelemetry logs, traces and metrics in a browser, on `127.0.0.1` only |
| `backup` | `postgres:17` + rclone, on a schedule | See Backups below |

**Docker:** keep Docker Desktop (WSL2 backend), but **update it**. 24.0.7 is from 2023. Set it to start when you sign in, and turn on Windows' "Use my sign-in info to automatically finish setting up after an update", so the stack comes back after Windows Update reboots.

**The GPU** isn't used: recognition runs on the iPhone with Apple's frameworks (ADR-017). It's there as a free upgrade path if the M3 evals ever show real misses, but that would mean going beyond Apple-only, so it'd be your call.

## Availability: what if the PC is off?

The phone does all the recognition itself, so the scanner keeps working when the PC is off or asleep:

- Barcodes and pictures are read on the phone, and the catalogue match waits as **Queued**.
- **"Do I own this?" still works**, because the phone keeps a synced copy of what you own (identifiers, titles and the feature prints of your items). This was moved into v1 for this reason.
- Everything resolves automatically when the PC is reachable again.

For results while you're out, the PC needs to be on. Either set Windows to **never sleep** (a gaming PC at idle draws roughly 70–120 W, so about **$8–15/month** of electricity if it's otherwise off a lot; much less extra if it's on most of the day anyway), or accept queued results until you're home. The same compose file runs unchanged on a small always-on mini PC later if you want.

## Environments

| Env | Where | Purpose |
|---|---|---|
| **Local dev** | Your PC, via **Aspire AppHost** (Postgres container, API, Expo web dev server) | Day-to-day development and tests |
| **CI** | GitHub-hosted runners, Testcontainers | Every PR: build, tests, scans, web E2E |
| **Production** | The same PC, via **Docker Compose** under `C:\CollectionMaxxing` | The app you use |

Dev and prod sit side by side without conflict: different compose project names, ports and volumes.

## Deployment (on merge to `main`)

```
GitHub-hosted runner                         Self-hosted runner on your PC (deploy job only)
───────────────────                          ───────────────────────────────────────────────
1. build & test (same as PR)
2. build web (expo export) → wwwroot
3. build api + migrate images → GHCR   ───►  4. docker compose pull
   (tag = git SHA)                           5. docker compose run --rm migrate   (expand-only migrations)
                                             6. docker compose up -d api           (~5 s restart)
                                             7. smoke test https://pc.tailnet.ts.net/health/ready
                                             8. failed? set the previous tag in .env → up -d (rollback)
```

- **Rollback** pins the previous image tag. Expand/contract migrations keep the previous image compatible with the new schema.
- **The self-hosted runner only runs the `deploy` job, for `main`, in this private repo.** PR code never runs on your PC. That matters, because a self-hosted runner executes whatever the workflow tells it to.
- **iPhone app:** see *Getting the app on your iPhone for $0* below.
- **CI minutes:** a private repo gets 2,000 free Linux minutes a month on GitHub-hosted runners, plenty for PR checks. GitHub's default **$0 spending limit** means overage is blocked, never billed. If you ever hit the cap, the PR checks can also run on your self-hosted runner, since only you push to this repo.

## Getting the app on your iPhone for $0

No Apple Developer Program ($99/yr), no Mac, no TestFlight:

1. **Build:** a GitHub Actions workflow on a macOS runner (it needs an image with Xcode 27, for iOS 27's Foundation Models) runs `expo prebuild` and `xcodebuild` with code signing disabled. It packages an **unsigned `.ipa`** as a workflow artifact. A private repo's free macOS budget is ~200 minutes a month, about 8–12 builds. Builds are only needed when native code or dependencies change, or for a release.
2. **Sign + install:** **AltServer** (free) runs on this PC and signs the `.ipa` with your **free Apple ID**; the **AltStore** app on your iPhone installs it.
3. **Keep it alive:** free signatures expire after **7 days**. AltStore re-signs in the background whenever the phone and the PC are on the same Wi-Fi, which happens every night at home. Your data survives re-signing.
4. **One-time:** turn on Developer Mode on the iPhone and trust your developer certificate.

**Limits that come with a free Apple ID:**

- 3 sideloaded apps (AltStore + CollectionMaxxing + a dev build);
- 10 app IDs a week;
- no paid-only capabilities: no push notifications, no Sign in with Apple, no Apple Private Cloud Compute.

The plan avoids all three. Sign-in uses passkeys and QR pairing ([07](07-authentication-and-authorization.md)); there are no push notifications (results wait in the app, and backup alerts come by email); hard cases go to Fix it. An **M0 spike** confirms that the on-device Foundation Model and Vision work in a free-signed app before we build on them.

## Backups and recovery

| What | How | Retention |
|---|---|---|
| Database | `pg_dump` (custom format) nightly at 3 a.m. → `F:\Backups\CollectionMaxxing` (a different drive from the Docker volume on C:) | 14 daily, 8 weekly, 12 monthly |
| Files (covers, your-photo covers) | Mirrored nightly to the same backup folder | Same |
| **Off-site** | Weekly **encrypted** copy (rclone `crypt`) to **Backblaze B2's free 10 GB** or a free OneDrive (5 GB). Your data (database dumps + covers) fits comfortably, so it's $0. | 12 weeks |
| Your own escape hatch | Export (CSV + JSON) from the app any time | — |
| Health | The backup job pings a free heartbeat check (Healthchecks.io); a missed night sends you an **email** | — |
| **Restore drill** | Restore into the dev stack and check the item counts. Done before v1 and then twice a year, using the runbook in `deploy/RESTORE.md`. | — |

Turn on **BitLocker** on the drives that hold the Docker volumes and backups, so a stolen or failed-and-recycled drive doesn't leak your data.

## Observability (light)

- **Aspire Dashboard** (standalone container) for logs, traces and metrics. One trace per scan goes from the phone through the API to the providers.
- **Logs** are also written to rotating files on the `files` volume, so they survive restarts.
- **Uptime:** optional. An Uptime Kuma container, or the Healthchecks.io ping, can notify you if `/health/ready` fails.

## Cost

| Item | Monthly |
|---|---|
| Hosting (your PC, Docker, Tailscale Personal, Aspire Dashboard) | **$0** |
| Electricity | ~$0–15, depending on how much the PC would be on anyway |
| Off-site backup (Backblaze B2 free 10 GB, or free OneDrive) | **$0** |
| Recognition (Apple frameworks on the phone) | **$0** |
| Apple Developer Program | **$0**: not needed; free Apple ID + AltStore sideloading |
| GitHub (private repo, Actions minutes, $0 spending limit) | **$0** |
| Domain | not needed; Tailscale provides the HTTPS name |

## Local developer setup (Windows, this PC)

1. Install the .NET 10 SDK, Node 22 LTS (or what Expo 57 recommends), an **updated Docker Desktop**, and **AltServer** (plus Apple's iTunes and iCloud for Windows, which it needs). Tailscale is already installed.
2. `git clone` → `dotnet user-secrets set …` for provider keys. Each is optional; fakes are used when a key is missing.
3. `dotnet run --project src/CollectionMaxxing.AppHost`: Postgres, the API (migrations applied) and Expo web, plus the Aspire dashboard.
4. iPhone: build the dev client with the macOS workflow, install it with AltStore, then `npx expo start` on the PC. The dev build reaches your PC over Tailscale too, so you can test away from home.
5. `dotnet test` runs everything except the on-phone evals. Docker must be running for Testcontainers.
6. Production setup: `deploy/README.md` covers the compose file, `.env`, `tailscale serve`, the runner and the backup schedule. It's about an hour, once.
