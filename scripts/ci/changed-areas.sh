#!/usr/bin/env bash
# Says which halves of the gate a change can affect: reads the changed paths (one per line, repository-relative, as
# `git diff --name-only` prints them) on stdin and prints two lines, `backend=true|false` and `app=true|false`.
# `false` means no file in the change can break that half, so its checks may be skipped; `true` means run them.
#
# Both start `true` and only an unambiguous change lowers one:
#   - every path under app/, none of them app/playwright/serve.mjs  ->  backend=false
#     (the backend suites read serve.mjs: SecurityHeadersTests holds the web server's headers to the API's own)
#   - every path under src/, tests/, deploy/ or evals/               ->  app=false
# Anything else (contracts/, docs/, .github/, .agents/, .claude/, scripts/, a file in the repository root, a change
# that spans both halves) and no paths at all leave both `true`, so a path this script has not heard of can only cost
# time, never skip a check.
#
# Used by the `changes` job of .github/workflows/ci.yml; tested by deploy/tests/changed-areas.test.sh.
#
# Usage:  git diff --name-only --no-renames HEAD^1 HEAD | bash scripts/ci/changed-areas.sh
set -u

backend=true
app=true
seen=0
all_app=1
all_server=1

while IFS= read -r path || [[ -n "$path" ]]; do
  path="${path%$'\r'}"
  [[ -n "$path" ]] || continue
  seen=1
  case "$path" in
    app/playwright/serve.mjs) all_app=0 ;;
    app/*) ;;
    *) all_app=0 ;;
  esac
  case "$path" in
    src/* | tests/* | deploy/* | evals/*) ;;
    *) all_server=0 ;;
  esac
done

if ((seen)); then
  ((all_app)) && backend=false
  ((all_server)) && app=false
fi

echo "backend=$backend"
echo "app=$app"
