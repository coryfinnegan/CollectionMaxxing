#!/usr/bin/env bash
# The quiet local gate: restore (locked), build (warnings are errors), format, test, and the app's own check if there is one.
#
# Prints one line per step (one line per test project inside the test phase). When a step fails it prints just what you
# need to fix it (the first lines of each error or failing test) and exits non-zero. Needs Docker running: the tests
# start a throwaway Postgres per test project that needs one.
#
# Run from anywhere:  bash scripts/check.sh [phase ...]
# Phases are restore, build, format, test and app; with no arguments all of them run. CI runs one phase per step so
# timings and failures localize (the backend job skips the app phase; the app job owns npm run check).
#
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
script_start=$SECONDS

# The phases to run. No arguments = the whole gate.
full_run=true
phases=("$@")
if [ "$#" -eq 0 ]; then
  phases=(restore build format test app)
else
  full_run=false
fi
for phase in "${phases[@]}"; do
  case "$phase" in
    restore|build|format|test|app) ;;
    *) echo "unknown phase '$phase' (want: restore build format test app)" >&2; exit 2;;
  esac
done
want() { local p="$1"; for q in "${phases[@]}"; do [ "$q" = "$p" ] && return 0; done; return 1; }

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
if want restore; then
run_step restore dotnet restore "$solution" --locked-mode -v q
if [ "$code" -ne 0 ]; then
  fail "restore (locked mode)" "$(grep -i 'error' "$log" | sort -u | head -n "$max_lines")"
fi
ok "restore (locked mode) (${seconds}s)"
fi

# ---- build -----------------------------------------------------------------------------------------------------
if want build; then
run_step build dotnet build "$solution" --no-restore -v q -warnaserror
if [ "$code" -ne 0 ]; then
  # The same error shows up once per project; keep each message once and drop the trailing [project] part.
  fail "build" "$(grep -E ': (error|warning) ' "$log" | sed -E 's/[[:space:]]*\[[^]]*\][[:space:]]*$//' | awk '!seen[$0]++' | head -n "$max_lines")"
fi
ok "build, warnings as errors (${seconds}s)"
fi

# ---- format ----------------------------------------------------------------------------------------------------
if want format; then
run_step format dotnet format "$solution" --verify-no-changes --no-restore
if [ "$code" -ne 0 ]; then
  fail "format (fix with: dotnet format CollectionMaxxing.slnx)" "$(grep ': error ' "$log" | sed -E 's/[[:space:]]*\[[^]]*\][[:space:]]*$//' | awk '!seen[$0]++' | head -n "$max_lines")"
fi
ok "format (${seconds}s)"
fi

# ---- test ------------------------------------------------------------------------------------------------------
if want test; then
# Every tests/ project except the two support libraries (see tests/Directory.Build.targets), each in its own process at
# the same time. A project that needs Postgres starts its own container, so lanes never see each other's data.
lanes=()
for proj in tests/*/*.csproj; do
  case "$(basename "$proj" .csproj)" in
    CollectionMaxxing.TestSupport|CollectionMaxxing.Api.TestKit) ;;
    *) lanes+=("$(basename "$proj" .csproj):$proj");;
  esac
done
test_wall_start=$SECONDS
export CM_PERF_LOG="$log_dir/postgres-perf.log"
rm -f "$CM_PERF_LOG"
pids=()
for lane in "${lanes[@]}"; do
  name="${lane%%:*}"
  proj="${lane#*:}"
  (
    start=$SECONDS
    dotnet test "$proj" --no-build --logger 'console;verbosity=minimal' >"$log_dir/test-$name.log" 2>&1
    echo "$?" >"$log_dir/test-$name.exit"
    echo "$((SECONDS - start))" >"$log_dir/test-$name.secs"
  ) &
  pids+=($!)
done
for pid in "${pids[@]}"; do wait "$pid"; done
test_wall=$((SECONDS - test_wall_start))

failed=0; passed=0; skipped=0; skipped_lanes=""
for lane in "${lanes[@]}"; do
  name="${lane%%:*}"
  log="$log_dir/test-$name.log"
  lane_code=$(cat "$log_dir/test-$name.exit")
  lane_seconds=$(cat "$log_dir/test-$name.secs")
  read -r lfailed lpassed lskipped < <(
    sed -nE 's/^(Passed|Failed)![[:space:]]+- Failed:[[:space:]]+([0-9]+), Passed:[[:space:]]+([0-9]+), Skipped:[[:space:]]+([0-9]+).*/\2 \3 \4/p' "$log" |
      awk '{f+=$1; p+=$2; s+=$3} END {print f+0, p+0, s+0}'
  )
  if [ "$lane_code" -ne 0 ] || [ "$lfailed" -gt 0 ]; then
    details=$(awk -v max="$max_lines" '
      /^[[:space:]]+Failed [^ ]/ { n = max; sub(/^[[:space:]]+/, ""); print; next }
      n > 1 && NF { n--; sub(/^[[:space:]]+/, "  "); print }
    ' "$log")
    # Nothing ran at all (a crash, or Docker is not running): show the tail instead.
    [ -n "$details" ] || details=$(grep -v '^[[:space:]]*$' "$log" | tail -n "$max_lines")
    fail "test $name ($lpassed passed, $lfailed failed, $lskipped skipped)" "$details"
  fi
  ok "test $name: $lpassed passed (${lane_seconds}s)"
  failed=$((failed + lfailed)); passed=$((passed + lpassed)); skipped=$((skipped + lskipped))
  if [ "$lskipped" -gt 0 ]; then skipped_lanes="$skipped_lanes${skipped_lanes:+, }$name"; fi
done
if [ "$skipped" -gt 0 ]; then
  log="$log_dir/test-CollectionMaxxing.Api.Tests.log"
  fail "test: $skipped tests were skipped in $skipped_lanes (skipped tests are not allowed)" ""
fi
if [ "$passed" -eq 0 ]; then
  log="$log_dir/test-${lanes[0]%%:*}.log"
  fail "test: no tests ran" "$(grep -v '^[[:space:]]*$' "$log" | tail -n "$max_lines")"
fi
ok "test: $passed passed in ${#lanes[@]} projects (${test_wall}s wall)"
if [ -f "$CM_PERF_LOG" ]; then
  containers=$(wc -l < "$CM_PERF_LOG")
  startup=$(sed -nE 's/.*ready in ([0-9.]+)s.*/\1/p' "$CM_PERF_LOG" | awk '{s+=$1} END {printf "%.1f", s}')
  echo "     postgres: $containers containers, ${startup}s combined startup (started concurrently)"
fi
fi

# ---- app (only once the Expo app exists) -----------------------------------------------------------------------
if want app && [ -f app/package.json ]; then
  run_step app npm run check --prefix app --silent
  if [ "$code" -ne 0 ]; then
    fail "app (npm run check)" "$(grep -v '^[[:space:]]*$' "$log" | tail -n "$max_lines")"
  fi
  ok "app: npm run check (${seconds}s)"
fi

if $full_run; then
  echo "all green ($((SECONDS - script_start))s)"
fi
