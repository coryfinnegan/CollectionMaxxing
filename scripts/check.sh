#!/usr/bin/env bash
# The quiet local gate: restore (locked), build (warnings are errors), test, and the app's own check if there is one.
#
# Prints one line per step. When a step fails it prints just what you need to fix it (the first lines of each error
# or failing test) and exits non-zero. Needs Docker running: the tests start a throwaway Postgres.
#
# Run from anywhere:  bash scripts/check.sh
# The full output of the failing step is kept in the file named in the failure message.
set -u

cd "$(dirname "${BASH_SOURCE[0]}")/.."

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

solution="CollectionMaxxing.slnx"
max_lines=15
log_dir="${TMPDIR:-/tmp}/collectionmaxxing-check"
mkdir -p "$log_dir"

# Sets: code, seconds, log. Runs the command with its output going to the step's log file.
run_step() {
  local name="$1"; shift
  log="$log_dir/$name.log"
  local start=$SECONDS
  "$@" >"$log" 2>&1
  code=$?
  seconds=$((SECONDS - start))
}

ok() { echo "ok   $1"; }

fail() {
  # $1 = headline, $2 = details (may be empty). Not used in a pipeline, so the exit really ends the script.
  echo "FAIL $1"
  if [ -n "${2:-}" ]; then
    printf '%s\n' "$2" | sed 's/^/     /'
  fi
  echo "     (full output: $log)"
  exit 1
}

# ---- restore ---------------------------------------------------------------------------------------------------
run_step restore dotnet restore "$solution" --locked-mode -v q
if [ "$code" -ne 0 ]; then
  fail "restore (locked mode)" "$(grep -i 'error' "$log" | sort -u | head -n "$max_lines")"
fi
ok "restore (locked mode) (${seconds}s)"

# ---- build -----------------------------------------------------------------------------------------------------
run_step build dotnet build "$solution" --no-restore -v q -warnaserror
if [ "$code" -ne 0 ]; then
  # The same error shows up once per project; keep each message once and drop the trailing [project] part.
  fail "build" "$(grep -E ': (error|warning) ' "$log" | sed -E 's/[[:space:]]*\[[^]]*\][[:space:]]*$//' | awk '!seen[$0]++' | head -n "$max_lines")"
fi
ok "build, warnings as errors (${seconds}s)"

# ---- format ----------------------------------------------------------------------------------------------------
run_step format dotnet format "$solution" --verify-no-changes --no-restore
if [ "$code" -ne 0 ]; then
  fail "format (fix with: dotnet format CollectionMaxxing.slnx)" "$(grep ': error ' "$log" | sed -E 's/[[:space:]]*\[[^]]*\][[:space:]]*$//' | awk '!seen[$0]++' | head -n "$max_lines")"
fi
ok "format (${seconds}s)"

# ---- test ------------------------------------------------------------------------------------------------------
run_step test dotnet test "$solution" --no-build --logger 'console;verbosity=minimal'
read -r failed passed skipped < <(
  sed -nE 's/^(Passed|Failed)![[:space:]]+- Failed:[[:space:]]+([0-9]+), Passed:[[:space:]]+([0-9]+), Skipped:[[:space:]]+([0-9]+).*/\2 \3 \4/p' "$log" |
    awk '{f+=$1; p+=$2; s+=$3} END {print f+0, p+0, s+0}'
)
if [ "$code" -ne 0 ] || [ "$failed" -gt 0 ]; then
  details=$(awk -v max="$max_lines" '
    /^[[:space:]]+Failed [^ ]/ { n = max; sub(/^[[:space:]]+/, ""); print; next }
    n > 1 && NF { n--; sub(/^[[:space:]]+/, "  "); print }
  ' "$log")
  # Nothing ran at all (a crash, or Docker is not running): show the tail instead.
  [ -n "$details" ] || details=$(grep -v '^[[:space:]]*$' "$log" | tail -n "$max_lines")
  fail "test ($passed passed, $failed failed, $skipped skipped)" "$details"
fi
if [ "$skipped" -gt 0 ]; then
  fail "test: $skipped tests were skipped (skipped tests are not allowed)" ""
fi
if [ "$passed" -eq 0 ]; then
  fail "test: no tests ran" "$(grep -v '^[[:space:]]*$' "$log" | tail -n "$max_lines")"
fi
ok "test: $passed passed (${seconds}s)"

# ---- app (only once the Expo app exists) -----------------------------------------------------------------------
if [ -f app/package.json ]; then
  run_step app npm run check --prefix app --silent
  if [ "$code" -ne 0 ]; then
    fail "app (npm run check)" "$(grep -v '^[[:space:]]*$' "$log" | tail -n "$max_lines")"
  fi
  ok "app: npm run check (${seconds}s)"
fi

echo "all green"
