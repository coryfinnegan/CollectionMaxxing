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

## Merging (one at a time)

1. Wait for CI on the PR's head commit. Poll `gh api repos/<repo>/commits/<sha>/check-runs` every 10 s, rather than using `gh pr checks --watch`, which can report an older run.
2. If the branch is behind and hot files conflict: rebase in the builder's worktree, regenerate the hot files (migration, lockfile, contract snapshot), run `scripts/check.ps1`, then `git push --force-with-lease` to the PR branch. Never force-push `main`.
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

- **Docker Desktop is 24.0.7 (API 1.43).** `PostgresFixture` pins the client to the daemon's own version. Don't hard-code it.
- **Aspire AppHost** opts out of NuGet lock files, because its packages are OS-specific.
- **`dotnet format`** occasionally crashes (Roslyn AccessViolation). Re-run it.
- **The contract snapshot's operation order** can change when endpoints move between route groups. Diff it semantically before worrying.
- **Stryker.NET can't run xUnit v3 yet.** Verifiers mutate by hand.

## End of a wave

1. Close the milestone.
2. Post a summary for the owner: what merged, what to test (the checkpoint), and roughly what it cost.
3. Draft the next wave's briefs with a **planner agent** (Sonnet, high effort, no worktree, creates issues labelled `status:blocked`) rather than writing them in the lead session.
4. Update the docs if reality diverged from the plan.
