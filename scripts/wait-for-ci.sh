#!/usr/bin/env bash
# Waits for the CI run of ONE commit and says whether that commit may be released.
#
# Used by .github/workflows/release.yml (the ci-gate job): the release workflow starts on the same push as CI, and the
# images may not be promoted to :main (which is what the deployer on the owner's PC deploys) until CI has succeeded
# for the very commit being released. It looks at the push run of ci.yml for that exact SHA; it never guesses from
# the branch, so a newer commit's green CI cannot vouch for an older, red one.
#
# Environment:
#   GH_REPO             owner/name (gh reads it too)                                         required
#   SHA                 the full 40-character commit being released                          required
#   GH_TOKEN            a token that may read workflow runs (actions: read)                  required by gh
#   CI_WORKFLOW         the CI workflow's file name                                          default ci.yml
#   CI_BRANCH           the branch CI ran on                                                 default main
#   CI_APPEAR_SECONDS   how long to wait for the CI run to exist at all                      default 600
#   CI_WAIT_SECONDS     how long to wait, in all, for it to finish                           default 3000
#   CI_POLL_SECONDS     the pause between looks                                              default 20
#   GITHUB_OUTPUT       when set, "passed=true" or "passed=false" is appended to it
#
# Exit 0 with passed=true:   CI succeeded for this commit.
# Exit 0 with passed=false:  CI was cancelled for this commit, which is what a newer push to main does to it (CI cancels
#                            the run it replaces). Nothing is released for this commit; the newer one carries on.
# Exit 1:                    CI failed or timed out, no CI run showed up, or GitHub could not be read for too long.
#                            It fails closed: unknown is never "go".
set -u

repo="${GH_REPO:?GH_REPO is not set}"
sha="${SHA:?SHA is not set}"
if [[ ! "$sha" =~ ^[0-9a-f]{40}$ ]]; then
  echo "::error::SHA '$sha' is not a full 40-character commit hash"
  exit 1
fi
workflow="${CI_WORKFLOW:-ci.yml}"
branch="${CI_BRANCH:-main}"
poll="${CI_POLL_SECONDS:-20}"
step=$((poll > 0 ? poll : 1))
appear_left=$(((${CI_APPEAR_SECONDS:-600} + step - 1) / step))
wait_left=$(((${CI_WAIT_SECONDS:-3000} + step - 1) / step))
((appear_left < 1)) && appear_left=1
((wait_left < appear_left)) && wait_left=$appear_left

result() { [[ -z "${GITHUB_OUTPUT:-}" ]] || echo "passed=$1" >>"$GITHUB_OUTPUT"; }

# The newest CI run of exactly this commit, as "status|conclusion|url"; nothing when there is none.
# The SHA was checked above, so it is safe to put into the jq text.
filter='.workflow_runs | map(select(.head_sha == "'"$sha"'")) | sort_by(.run_number) | last | if . == null then "" else "\(.status)|\(.conclusion // "none")|\(.html_url)" end'

seen=0
while true; do
  line="$(gh api "repos/$repo/actions/workflows/$workflow/runs?head_sha=$sha&event=push&branch=$branch&per_page=30" --jq "$filter" 2>/dev/null)"
  rc=$?
  if ((rc != 0)) || [[ -z "$line" ]]; then
    if ((seen == 0)); then
      if ((rc != 0)); then echo "could not read the CI runs yet (gh exited $rc)"; else echo "no $workflow run for ${sha:0:12} yet"; fi
      appear_left=$((appear_left - 1))
      if ((appear_left <= 0)); then
        echo "::error::No $workflow run for ${sha:0:12} showed up (or GitHub could not be read), so the release is held back."
        exit 1
      fi
    else
      echo "could not read the CI run just now (gh exited $rc); looking again"
    fi
  else
    seen=1
    IFS='|' read -r status conclusion url <<<"$line"
    if [[ "$status" == "completed" ]]; then
      case "$conclusion" in
        success)
          echo "::notice::CI succeeded for ${sha:0:12}: $url"
          result true
          exit 0
          ;;
        cancelled)
          echo "::notice::CI was cancelled for ${sha:0:12} (a newer push replaced it), so this commit is not released: $url"
          result false
          exit 0
          ;;
        *)
          echo "::error::CI ended '$conclusion' for ${sha:0:12}, so the release is held back: $url"
          exit 1
          ;;
      esac
    fi
    echo "CI for ${sha:0:12} is $status: $url"
  fi
  wait_left=$((wait_left - 1))
  if ((wait_left <= 0)); then
    echo "::error::CI for ${sha:0:12} did not finish in time, so the release is held back."
    exit 1
  fi
  sleep "$poll"
done
