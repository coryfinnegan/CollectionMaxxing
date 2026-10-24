# Lead runbook

How the lead session runs a wave ([13](../13-agent-orchestration.md) explains why). Start a **fresh session for each wave**, on **Sonnet**. The state lives in GitHub: issues are the briefs, labels are `wave:N` / `stream:X` / `tier:X` / `status:blocked`, and there's one milestone per wave.

## Start of a wave

1. `gh issue list --milestone "Wave N" --state open` to see what's planned.
2. The owner approves the wave plan (D16). Then remove `status:blocked` from the tasks that can start now.
3. Launch at most **4 builders** at once, picking the ones whose dependencies have merged.

## Launching a builder

Use the Agent tool:

- `model`: `sonnet`, or `haiku` for `tier:mechanical`;
- `effort`: `high` for critical, `medium` for standard, `low` for mechanical;
- `isolation: "worktree"`, `run_in_background: true`.

Prompt skeleton:

> You are a builder agent on CollectionMaxxing, in your own git worktree of github.com/coryfinnegan/CollectionMaxxing (public repo). Read `CLAUDE.md` first and follow it exactly. Your brief is issue #N: `gh issue view N --comments --repo coryfinnegan/CollectionMaxxing`. This is the owner's personal PC: stay inside your worktree (package caches are fine); Docker is running for Testcontainers; never bind ports 8080, 8409, 32400, 8096. Steps: `git fetch origin && git checkout -b wN/<task>-<slug> origin/main`; build test-first; `pwsh -NoProfile -File scripts/check.ps1` green; commit with the CLAUDE.md trailer; push; `gh pr create` with `Closes #N`; `gh pr checks --watch` (≤ 10 min per call) until green. Reply in ≤ 12 lines: PR URL, CI, notes for the lead, anything blocked.

## Verifying a PR

- **Critical tier:** a verifier agent (Sonnet, high effort, worktree, background). It reports only and never pushes. Prompt skeleton:

  > You are the verifier for PR #P. Try to break it; report only (never push, commit or comment). `git fetch origin && git checkout --detach origin/<branch>`. Read CLAUDE.md, the brief (with comments) and the PR body. Read the diff selectively (`git diff --stat origin/main...HEAD` first). Check: scope; authorization (IDOR, 404/403, fail-closed, matrix rows); the brief's locked acceptance tests exist and bite. Stryker can't run with xUnit v3, so break 3 key rules by hand, check a test fails each time, and revert. Run the gates; check conventions and hot files. Reply in ≤ 25 lines, starting PASS or FAIL; each finding gets a severity, file:line and a failure scenario.

- **Standard tier:** the lead reviews the diff itself, looking at dependencies, secrets, authorization on new endpoints and hot files. Send it to a verifier only if something looks off.
- **Docs-only and config-only PRs:** no verifier.
- **Findings:** a *major* finding that's a trap for later agents gets fixed before the next wave builds on it. Small fixes go to a fresh small builder (FND-1b and FND-3b were examples). Never resume a large builder just for a small fix, because reloading its context costs more.
- **Fix builders get their own worktree.** The lead session can't write into another agent's worktree, and a subagent launched without `isolation: "worktree"` inherits that block. Launch the fix builder with isolation, have it `git checkout -B fix/<slug> origin/<pr-branch>`, and push with `git push origin HEAD:<pr-branch>` (fast-forward only).
- **Builders that finish but can't reproduce** (a CI-only flake, say) get two rounds at most, then park the issue with the evidence in a comment.

## Merging (one at a time)

1. Wait for CI on the PR's head commit. Poll `gh api repos/<repo>/commits/<sha>/check-runs` every 10 s, rather than using `gh pr checks --watch`, which can report an older run.
2. If the branch conflicts with `main`: `git fetch origin main && git merge FETCH_HEAD` on the PR branch (never rebase or force-push; the app's Auto-fix expects merges). Keep both sides, regenerate the hot files (migration, lockfile, contract snapshot), run `scripts/check.ps1`, then push normally. If the PR's builder is still running, send it the conflict instead of merging yourself.
3. `gh pr merge <P> --squash --delete-branch --subject "[WN][TASK] Summary (#P)"`.
4. If a task depended on this one, remove its `status:blocked` label.
5. Clean up the worktree:
   - `git -c core.longpaths=true worktree remove --force .claude/worktrees/<agent>`
   - `git branch -D -q worktree-<agent> <branch>`
   - If `node_modules` paths are too long, delete the leftover folder with PowerShell: `Remove-Item -LiteralPath ('\\?\' + $path) -Recurse -Force`. Check the parent folder is `.claude\worktrees` first.
6. `git pull --ff-only` in the main checkout.

## iOS builds

- `gh workflow run ios-build.yml --ref main` builds on the `xcode-27` runner (iOS 27 SDK). It's free on a public repo.
- Only the lead triggers builds, plus NAT tasks on their own branch (at most 4 per task).
- `npm run generate` must run before bundling, because the generated client is gitignored.

## Known quirks

- **The deployer reads the compose file from git at the release's commit** (#246), not the one on disk. A release that needs a `.env` variable the host lacks is refused before anything migrates; add the variable, then let the next 2-minute run retry.
- **Flaky app Jest test (#260):** `LibraryGrid.test.tsx` fails now and then, mostly in CI's backend job. Re-run the failed job before investigating.
- **Docker Desktop is 24.0.7 (API 1.43).** `PostgresFixture` pins the client to the daemon's own version. Don't hard-code it.
- **Aspire AppHost** opts out of NuGet lock files, because its packages are OS-specific.
- **`dotnet format`** occasionally crashes (Roslyn AccessViolation). Re-run it.
- **Regenerating the contract on this PC:** use the update command the contract test prints. If a build is needed, `dotnet build -p:UpdateContract=true` must run with `ASPNETCORE_ENVIRONMENT=Development`, or it fails on the key-ring check.
- **The contract snapshot's operation order** can change when endpoints move between route groups. Diff it semantically before worrying.
- **Stryker.NET can't run xUnit v3 yet.** Verifiers mutate by hand.
- **Docker Desktop may be off** after a reboot. Testcontainers then fails with "Failed to connect to Docker endpoint". Check `docker version` before launching builders, and start Docker Desktop if needed (no admin).
- **Logs of a CI attempt that was re-run:** `gh run view --log` shows nothing for the failed attempt. Use `gh api repos/<repo>/actions/runs/<id>/attempts/1/jobs`, then `gh api repos/<repo>/actions/jobs/<job>/logs`.
- **A worktree that won't delete** is usually held by a leftover process from its agent (for example `node playwright/serve.mjs`). Stop only the processes whose command line points into that worktree, then remove it.
- **Tests that start a real `JobWorker`** race other test hosts in the shared database. Remove the hosted worker in the test host and run jobs explicitly through `JobRunner` (SCAN-1a's `TestApiFactory` does this).
- **Windows Firewall prompts** come from any test server that listens beyond loopback. Each worktree builds its own `*.Tests.exe`, so the owner gets one prompt per worktree. Main is loopback-only (`LoopbackWireMock`, guarded by `TestServerBindingTests`), but an older branch isn't: merge `origin/main` into a branch before any agent runs tests on it. `Get-NetFirewallApplicationFilter` (no admin needed) shows which worktree exes prompted.
- **Local-only flakes under load:** with 3–4 agents testing at once, a few timing-bound backend tests (`UpcQuotaTests`, `ScanLearningTests`) can fail once and pass on re-run (#136). Tell builders up front, so they don't chase them.

## Lessons from Wave 4

- **Do lead PRs in a scratch worktree** (`git worktree add -B lead/<slug> .claude/worktrees/lead-scratch origin/main`), never by switching the lead session's own branch. An agent can end up working in the lead's worktree; a branch switch there broke its run and caused a git index-lock error.
- **Check what CI can't see before merging:**
  - `npx expo config --type introspect` after any `app.config.ts` change. Expo's config loader can't import another `.ts` file from `app.config.ts`, and `npm run check` doesn't load the config.
  - The real web export against the enforced CSP: the inline-script hash test.
  - Fallback routes vs middleware-served paths: `/.well-known` for OpenIddict (Wave 6: `/signin-oidc`, which the web app's 404 would otherwise swallow).
- **Scripts run on Linux CI too:** `Join-Path` with a `C:\` root throws "Cannot find drive" on Linux, and containers running as another uid can't write runner-created bind mounts (`chmod a+rwX` the per-run folders).
- **Verifiers earn their keep on lockout paths:** rate limits that counted token refreshes, and a client that treated a 429 as "revoked", would have un-paired the owner's phone. Brief verifiers to try to lock the owner out.
- **Builders under load:** tell every builder which local-only flakes to ignore, and give tests a real-time budget that can't expire under multi-agent load (`ResolveBudget` 2 minutes in test hosts; no hosted `JobWorker` in test apps).

## Lessons from Wave 6

- **A wave that must ship as one change uses an integration branch** (`w6/auth0`), because the deployer ships every push to `main`. Builder PRs target it, and one PR lands it on `main`.
  - A PR into another branch doesn't auto-close its issue (`Closes #N` only works against the default branch). Close issues by hand.
  - CI still runs on those PRs: `ci.yml` has no branch filter.
  - `workflow_dispatch` only works for a workflow file that is on `main`, so a new manual workflow (`ios-check.yml`) lands there first.
- **A deploy that adds a required `.env` setting needs the clone first.** Until #253, the deployer started a release with the previous release's compose file, so the Wave 6 cutover rolled back once. Before a cutover, run `docker compose config` with the real `.env` against the new compose file, and know the recovery path:
  - Disable the `CollectionMaxxingDeployer` task, then fast-forward `C:\CollectionMaxxing\repo` to the release.
  - A rolled-back sha is marked in `state\deployer-state.json` and skipped. Clear `failedSha` with `Read-CmStateStrict` / `Write-CmState` (dot-source `Cm.Common.ps1` **and** `Cm.Deploy.ps1`), never by hand-writing the JSON, and copy the file first. Writing JSON by hand once left it as `null`.
  - Re-enable the task and let it deploy. The session's permission classifier refuses a manual `Deploy-CollectionMaxxing.ps1 -Apply`.
- **Wipe cutovers:** take the backup, copy it into its own `pre-auth0`-style folder outside nightly retention (a nightly run prunes by age), run `scripts\restore-drill.ps1 -ConfirmLive` on it, and move old secrets and `.env` there rather than deleting them. Empty volumes from a throwaway container that mounts only that volume; never remove one.
- **Auth0 on the Free plan:**
  - The refresh-token endpoints answer 403 `feature_not_enabled`. Revoke by deleting the person's grants (`DELETE /api/v2/grants?user_id=`, scope `delete:grants`).
  - Connection options are hidden and locked without the `read:connections_options` and `update:connections_options` scopes.
  - App descriptions are limited to 140 characters.
  - Auth0 may store a value in another order than the file (the Apple `scope`), which shows as a change on every dry run.
  - Run `Apply-Auth0.ps1` from PowerShell, not Git Bash: the CLI looked in another home folder and couldn't find its login.
- **Consoles in the built-in browser** (Google Cloud, Apple Developer): sessions expire during long waits, so the owner signs in again. Secrets that show once (the Google client secret, the Apple `.p8`) are downloaded by the owner and moved into the secrets folder by script. Never read them off the page. Use a **new** project and a **separate** App ID rather than editing the ones that ship the app.
- **Verifiers found real problems again.** ASP.NET's sliding cookie renews only after half its lifetime, so "30 days idle" was really 15 to 30. Brief verifiers on every sign-in change to probe the *lifetime* claims as well as the access rules.
- **An edit hook can refuse writes into another worktree.** If a scratch worktree for lead PRs is refused (this overrides the Wave 4 advice), branch inside the lead's own worktree instead. No agent works there.
- **Check a done-criterion against the system, not against the owner's word.** "Signed in on the phone" is a row in `app_sign_ins`. Count rows (no personal data) before closing a wave.

## When the session limit hits

- In-process agents stop with their work saved. In the **same** lead session, resume each with `SendMessage` ("the limit has reset; continue from …") rather than starting a new agent.
- A **new** lead session can't reach the old agents. Save each one's uncommitted work as a patch (`git -C <worktree> diff HEAD`, plus `git diff --no-index /dev/null <file>` for new files), commit it to a `wip/<task>` branch from the lead's own worktree, push it, and start a fresh agent from that branch. Remove the old worktree first: a branch checked out there can't be checked out again.
- Agents that wait on CI can re-send their report every time a wait ends. Tell them once to stop polling.

## End of a wave

1. Close the milestone.
2. Post a summary for the owner: what merged, what to test (the checkpoint), and roughly what it cost.
3. Draft the next wave's briefs with a **planner agent** (Sonnet, high effort, no worktree, creates issues labelled `status:blocked`) rather than writing them in the lead session.
4. Update the docs if reality diverged from the plan.
