# v1.0 release and the week of real use

## When to use this

When every wave is merged and you are ready to cut v1.0, install it, and live with it for a real week (the M5 exit criteria in `docs/11-roadmap.md`). This page is also the **owner's list of every hands-on step still pending**: tick the boxes as you go and keep the page open on your phone. Where a step has its own document, this page points to it instead of repeating it.

## Steps

1. **Cut the release.** From a clean `main` that CI has passed, tag it. The tag starts `.github/workflows/ios-build.yml` on a macOS runner; it builds the unsigned `.ipa`, creates the GitHub release and attaches the `.ipa` and its `.sha256` file:
   ```powershell
   git -C C:\CollectionMaxxing\repo fetch origin main
   git -C C:\CollectionMaxxing\repo tag v1.0.0 origin/main
   git -C C:\CollectionMaxxing\repo push origin v1.0.0
   gh run list --workflow ios-build.yml --limit 3
   ```
   (Or ask the lead to tag.) The PC picks up the new server image by itself within a few minutes; if it does not, see [update-and-rollback.md](update-and-rollback.md).
2. **Install the build.** The deployer copies the `.ipa` into the web app after the next good deploy: open **Settings > Get the iPhone app**, or download it from the release. Install it with AltStore ([altstore-resigning.md](altstore-resigning.md)), turn on Developer Mode, trust the certificate.
3. **Turn on VPN On Demand** in the Tailscale app so the phone connects by itself when CollectionMaxxing needs the server ([tailscale-and-https.md](tailscale-and-https.md), step 6).
4. **Work through the checklist below** over the first days, then use the app for a real week at home and out (a thrift store over cellular).
5. **Every bug becomes a failing test, then a fix.** When something is wrong:
   1. write down what you did, what you expected and what happened (a screenshot is fine, no photos of anyone, no secrets);
   2. open a GitHub issue with that text and the app version (Settings > About) and the time;
   3. the builder agent reproduces it as a failing test first, then makes it pass, then the fix ships as a normal release that the deployer installs;
   4. you update the app from the web page again and check the same steps now work.
6. **Close out the week.** Run the restore drill once more (it is on the list), copy the last PASS lines into the drill log, and tell the lead the week is done.

## The owner's checklist

Date started: ______

**On the phone**

- [ ] Install the latest iOS build (above) and run each pull request's on-phone checklist. They are in the PR descriptions under "Check at the next checkpoint" (for example the pairing, scanner and review flows); list the merged ones with `gh pr list --state merged --limit 30` and tick each one off in its PR comments.
- [ ] **Golden photos and Run evals (the M3 gate).** Take the photos: 40 to 60 shelf photos and 60 to 100 single items, into `C:\CollectionMaxxing\evals\golden\shelf` and `...\golden\single` (never inside the repo). Make the empty truth files with `pwsh -NoProfile -File C:\CollectionMaxxing\repo\scripts\eval-truth-stub.ps1 <folder>`, then fill them in. In the dev build on the phone open **Run evals** and let both engines run. The scoring and the pass criteria are in `docs/09-testing-and-tdd.md` (AI evals) and `docs/02-scanning-and-recognition.md`.
- [ ] **VoiceOver, Larger Text and Reduce Motion pass** (about 30 minutes): follow [../../docs/accessibility/ios-checklist.md](../../docs/accessibility/ios-checklist.md) and tick it there.
- [ ] **AltStore re-signing check.** Confirm the expiry date in AltStore > My Apps moves forward on its own over a couple of nights at home, on your phone and on a family member's ([altstore-resigning.md](altstore-resigning.md)). Note the date it first re-signed itself: ______

**On the PC**

- [ ] **Restore drill.** Closes the backup story. About 2 minutes; Docker Desktop must be running and last night's backup should exist:
  ```powershell
  pwsh -NoProfile -File C:\CollectionMaxxing\repo\scripts\restore-drill.ps1 -ConfirmLive
  ```
  Paste the `RESULT:` and PASS lines into a new file under `deploy/drills/` using the template in [../drills/README.md](../drills/README.md). More: [../RESTORE.md](../RESTORE.md).
- [ ] **k6 load check, `gaming` then `full`.** Details in [../../docs/perf/README.md](../../docs/perf/README.md). Build the images before the game starts, then run each with the game going:
  ```powershell
  pwsh -NoProfile -File C:\CollectionMaxxing\repo\scripts\perf\Run-Perf.ps1 -BuildOnly
  pwsh -NoProfile -File C:\CollectionMaxxing\repo\scripts\perf\Run-Perf.ps1 -Profile gaming
  pwsh -NoProfile -File C:\CollectionMaxxing\repo\scripts\perf\Run-Perf.ps1 -Profile full -ConfirmFull
  ```
  Stop at any time with `pwsh -NoProfile -File C:\CollectionMaxxing\repo\scripts\perf\Stop-Perf.ps1`. Fill in the results table at the end of that document (p95, errors, did the game stutter).
- [ ] **Confirm the TrustedProxies range.** The compose file trusts `172.16.0.0/12` for the client address (`Auth__RateLimit__TrustedProxies__0`). Check Docker's network really lies inside it (172.16.x.x to 172.31.x.x). These two commands only read:
  ```powershell
  docker network ls --filter name=collectionmaxxing
  docker network inspect collectionmaxxing_default --format '{{range .IPAM.Config}}{{.Subnet}} {{end}}'
  ```
  If the subnet printed is outside that range, tell the lead before changing anything. Then sign in from the phone over cellular and check the sign-in event in the Aspire dashboard (`http://127.0.0.1:18888`) shows a `100.` Tailscale address, not the Docker gateway.
- [ ] Optional: refresh the security evidence when the release is out: `pwsh -NoProfile -File C:\CollectionMaxxing\repo\scripts\security\Run-ZapBaseline.ps1` and the others listed at the end of [../../docs/security/asvs-l2-checklist.md](../../docs/security/asvs-l2-checklist.md).

**On the web**

- [ ] **After the next release, open `https://pc.tailnet.ts.net/`** in the browser you use. The API now serves the web app from the same address, so this is the one place to go.
- [ ] **The one-time web re-login.** Web sign-in now uses sessions, so you are asked to sign in once more: use your **passkey** (Windows Hello or Face ID). Family members do the same on their first visit. If the passkey prompt does not appear, see "Recovery" in `docs/07-authentication-and-authorization.md` and [lost-phone-or-remove-member.md](lost-phone-or-remove-member.md).

**The loop for the week**

- [ ] Every wrong thing became an issue with the steps to reproduce (step 5 above).
- [ ] No data-loss scares; a spot check of items and covers after the nightly backup, and a green heartbeat each morning.

## How to know it worked

- The release exists on GitHub with the `.ipa` and its `.sha256`, the PC is running its commit (`current.sha` in `deployer-state.json`), and `https://pc.tailnet.ts.net/health/ready` answers `Healthy`.
- The app on your phone opens and re-signs itself without you doing anything.
- Every box above is ticked, the drill log has a `PASS` line from this week, and the k6 and accessibility results are written down where their documents say.
- A week of real use ended with every bug either fixed by a release or sitting in an issue with a failing test.
