#!/usr/bin/env bash
# Tests for scripts/ci/changed-areas.sh (which halves of the gate a pull request's files can affect). Prints "PASS name"
# or "FAIL name: why" per test, then a summary line. Feeds the script path lists on stdin; touches nothing else.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
script="$here/../../scripts/ci/changed-areas.sh"

pass=0
fail=0

ok() { pass=$((pass + 1)); echo "PASS $1"; }
bad() { fail=$((fail + 1)); echo "FAIL $1: $2"; }
check() { if [[ "$2" == "$3" ]]; then ok "$1"; else bad "$1" "expected [$2] but got [$3]"; fi; }

# classify "path1\npath2..." -> "backend=X app=Y" (one line, so a test reads as one expectation)
classify() { printf '%b' "$1" | bash "$script" | tr '\n' ' ' | sed 's/ $//'; }

BOTH="backend=true app=true"
NO_BACKEND="backend=false app=true"
NO_APP="backend=true app=false"

# ---- an app-only change skips the backend ----------------------------------------------------------------------------
check "areas: one app file leaves the backend out" "$NO_BACKEND" "$(classify 'app/src/features/home/Home.tsx\n')"
check "areas: many app files, config and tests included, leave the backend out" "$NO_BACKEND" \
  "$(classify 'app/package.json\napp/app.config.ts\napp/src/ui/Button.tsx\napp/playwright/visual.spec.ts\napp/playwright.config.ts\n')"
check "areas: the last line needs no newline" "$NO_BACKEND" "$(classify 'app/src/a.ts')"
check "areas: Windows line endings do not count as part of the path" "$NO_BACKEND" "$(classify 'app/src/a.ts\r\napp/src/b.ts\r\n')"

# ---- serve.mjs is read by a backend test ------------------------------------------------------------------------------
check "areas: app/playwright/serve.mjs alone runs everything (SecurityHeadersTests reads it)" "$BOTH" "$(classify 'app/playwright/serve.mjs\n')"
check "areas: serve.mjs among other app files runs everything" "$BOTH" "$(classify 'app/src/a.ts\napp/playwright/serve.mjs\napp/src/b.ts\n')"
check "areas: a neighbour of serve.mjs is still app-only" "$NO_BACKEND" "$(classify 'app/playwright/serve.test.mjs\napp/playwright/csp.spec.ts\n')"

# ---- a server-only change skips the app and e2e -------------------------------------------------------------------------
check "areas: src/ alone leaves the app out" "$NO_APP" "$(classify 'src/CollectionMaxxing.Api/Program.cs\n')"
check "areas: tests/ alone leaves the app out" "$NO_APP" "$(classify 'tests/CollectionMaxxing.Domain.Tests/ItemTests.cs\n')"
check "areas: deploy/ alone leaves the app out" "$NO_APP" "$(classify 'deploy/agent/agent-prompt.md\n')"
check "areas: evals/ alone leaves the app out" "$NO_APP" "$(classify 'evals/baseline.json\n')"
check "areas: src, tests, deploy and evals together leave the app out" "$NO_APP" \
  "$(classify 'src/a.cs\ntests/b.cs\ndeploy/c.ps1\nevals/d.json\n')"

# ---- anything else runs everything ---------------------------------------------------------------------------------------
check "areas: contracts/ runs everything (the app client is generated from it)" "$BOTH" "$(classify 'contracts/openapi.json\n')"
check "areas: docs/ runs everything" "$BOTH" "$(classify 'docs/01-architecture.md\n')"
check "areas: .github/ runs everything" "$BOTH" "$(classify '.github/workflows/ci.yml\n')"
check "areas: .agents/ runs everything" "$BOTH" "$(classify '.agents/skills/x/SKILL.md\n')"
check "areas: .claude/ runs everything" "$BOTH" "$(classify '.claude/skills/x/SKILL.md\n')"
check "areas: scripts/ runs everything" "$BOTH" "$(classify 'scripts/check.sh\n')"
check "areas: a root file runs everything" "$BOTH" "$(classify 'CLAUDE.md\n')"
check "areas: Directory.Packages.props in the root runs everything" "$BOTH" "$(classify 'Directory.Packages.props\n')"
check "areas: a path that only starts like app/ is not the app" "$BOTH" "$(classify 'application/readme.md\n')"
check "areas: a path that only starts like src/ is not the server" "$BOTH" "$(classify 'srcs/a.cs\n')"
check "areas: a quoted path (git quotes odd names) runs everything" "$BOTH" "$(classify '"app/src/caf\\303\\251.ts"\n')"

# ---- a change that spans both halves runs everything ------------------------------------------------------------------
check "areas: app and src together run everything" "$BOTH" "$(classify 'app/src/a.ts\nsrc/CollectionMaxxing.Api/Program.cs\n')"
check "areas: app and tests together run everything" "$BOTH" "$(classify 'app/src/a.ts\ntests/x/y.cs\n')"
check "areas: an app change with one docs file runs everything" "$BOTH" "$(classify 'app/src/a.ts\ndocs/x.md\n')"
check "areas: a server change with one scripts file runs everything" "$BOTH" "$(classify 'src/a.cs\nscripts/check.sh\n')"
check "areas: a server change with one app file runs everything" "$BOTH" "$(classify 'src/a.cs\napp/src/a.ts\n')"

# ---- nothing to go on runs everything ---------------------------------------------------------------------------------
check "areas: no input at all runs everything" "$BOTH" "$(printf '' | bash "$script" | tr '\n' ' ' | sed 's/ $//')"
check "areas: blank lines only run everything" "$BOTH" "$(classify '\n\n')"

# ---- the output shape the workflow reads -------------------------------------------------------------------------------
check "areas: output is exactly two key=value lines, backend first" "backend=false|app=true" \
  "$(printf 'app/src/a.ts\n' | bash "$script" | tr '\n' '|' | sed 's/|$//')"

echo "SUMMARY passed=$pass failed=$fail"
((fail == 0))
