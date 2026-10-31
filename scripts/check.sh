#!/usr/bin/env bash
# The quiet local gate: restore (locked), build (warnings are errors), format, test, and the app's own check if there is one.
#
# Prints one line per step (one line per test project inside the test phase). When a step fails it prints just what you
# need to fix it (the first lines of each error or failing test) and exits non-zero. Needs Docker running: the tests
# start a throwaway Postgres per test project that needs one.
#
# The format phase also runs scripts/check-comments.mjs: comments under src/ say what the code does and why, not where
# the idea came from (the paths it holds to that are listed in scripts/comment-history-enforced.txt).
#
# Run from anywhere:  bash scripts/check.sh [phase ...]
# Phases are restore, build, format, test and app; with no arguments all of them run. CI runs one phase per step so
# timings and failures localize (the backend jobs skip the app phase; the app job owns npm run check).
#
# CM_TEST_ONLY limits the test phase to the named test projects (whitespace-separated basenames, e.g.
# CM_TEST_ONLY="CollectionMaxxing.Domain.Tests"). CI's backend-test matrix sets it so each shard runs its own
# projects; unset, every test project runs. All the usual guards still apply to the lanes that run.
#
# CM_TEST_FILTER hands a VSTest filter expression (`dotnet test --filter`; xUnit v3 runs in its VSTest flavour here,
# see tests/Directory.Build.targets) to every selected project; CI uses it with CM_TEST_ONLY to shard the two slow Api
# suites across runners. Use FullyQualifiedName~ / !~ (contains / does not contain) and DisplayName~ / !~, joined with
# & and | and grouped with (). A filter that matches nothing fails, because an empty shard would hide a coverage gap.
# CM_SHARD_REPORT (a file path, with CM_TEST_FILTER) records "project listed-in-shard listed-in-suite ran" per
# project; CI uploads it so the backend job can prove the shards add up to the whole suite (see ci.yml).
#
# The full output of the failing step is kept in the file named in the failure message. The logs live in a folder of this
# checkout's own under the temp folder (collectionmaxxing-check/<folder name>-<checksum of the path>), so gate runs in
# parallel worktrees never read each other's logs.
set -u

cd "$(dirname "${BASH_SOURCE[0]}")/.."

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

solution="CollectionMaxxing.slnx"
max_lines=15

# Agents run this gate at the same time in different worktrees of one PC, so the logs cannot share a folder: a run
# would read the format.log another run just wrote. The folder name is the checkout's folder name (so a person can tell
# which is which) plus a checksum of its full path (so two worktrees that end in the same name still differ). The same
# checkout gets the same folder every run.
checkout_root="$(pwd -P)"
checkout_name="$(basename "$checkout_root")"
checkout_name="${checkout_name//[^A-Za-z0-9._-]/_}"
checkout_id="$(printf '%s' "$checkout_root" | cksum | cut -d' ' -f1)"
log_dir="${TMPDIR:-/tmp}/collectionmaxxing-check/${checkout_name:0:40}-$checkout_id"
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
# One project at a time, not solution-wide: `dotnet format` intermittently crashes inside Roslyn's analyzer-config
# lookup (OneToOneUnicodeComparer.GetHashCode via AnalyzerFormatter.FilterAnalyzersAsync: NullReferenceException on
# the .slnx, IndexOutOfRangeException per-project; see issue #308). Per-project runs crash less often, and a run that
# does crash is retried below; only output with the CLR crash banner is retried, so genuine formatting violations
# still fail immediately. Same coverage as the solution-wide run.
log="$log_dir/format.log"
: > "$log"
start=$SECONDS
failed=""
max_attempts=3
for proj in src/*/*.csproj tests/*/*.csproj; do
  attempt=0
  verified=false
  while [ "$verified" = false ] && [ "$attempt" -lt "$max_attempts" ]; do
    attempt=$((attempt + 1))
    attempt_log="$log_dir/format-attempt.log"
    dotnet format "$proj" --verify-no-changes --no-restore >"$attempt_log" 2>&1
    code=$?
    cat "$attempt_log" >>"$log"
    if [ "$code" -eq 0 ]; then
      verified=true
    elif grep -q 'Unhandled exception' "$attempt_log"; then
      if [ "$attempt" -lt "$max_attempts" ]; then
        echo "     format: $(basename "$proj" .csproj) hit the Roslyn crash, retrying (attempt $((attempt + 1))/$max_attempts; see issue #308)"
      fi
    else
      break
    fi
  done
  [ "$verified" = true ] || failed="$failed${failed:+, }$(basename "$proj" .csproj)"
done
rm -f "$log_dir/format-attempt.log"
seconds=$((SECONDS - start))
if [ -n "$failed" ]; then
  details="$(grep ': error ' "$log" | sed -E 's/[[:space:]]*\[[^]]*\][[:space:]]*$//' | awk '!seen[$0]++' | head -n "$max_lines")"
  [ -n "$details" ] || details="$(grep -v '^[[:space:]]*$' "$log" | tail -n "$max_lines")"
  fail "format ($failed; fix with: dotnet format <project>.csproj)" "$details"
fi
ok "format (${seconds}s)"

# Comments say what the code does and why, not where the idea came from. The paths held to that rule are listed in
# scripts/comment-history-enforced.txt (see the header there for how to widen it).
if ! command -v node >/dev/null 2>&1; then
  log="$log_dir/comments.log"
  fail "comments (node is needed for scripts/check-comments.mjs)" ""
fi
run_step comments node scripts/check-comments.mjs
if [ "$code" -ne 0 ]; then
  fail "comments (say what the code does and why; to widen the ratchet see scripts/comment-history-enforced.txt)" "$(head -n "$max_lines" "$log")"
fi
ok "comments, history-free in the enforced paths (${seconds}s)"
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
if [ -n "${CM_TEST_ONLY:-}" ]; then
  keep=()
  for lane in "${lanes[@]}"; do
    name="${lane%%:*}"
    for want_name in $CM_TEST_ONLY; do
      if [ "$want_name" = "$name" ]; then keep+=("$lane"); break; fi
    done
  done
  if [ "${#keep[@]}" -eq 0 ]; then
    echo "test: CM_TEST_ONLY matched no test projects ('$CM_TEST_ONLY')" >&2
    exit 2
  fi
  lanes=("${keep[@]}")
fi
test_filter="${CM_TEST_FILTER:-}"
test_args=(--no-build --logger 'console;verbosity=minimal')
if [ -n "$test_filter" ]; then
  test_args+=(--filter "$test_filter")
  # Discovery only (nothing runs, no Docker needed): how many tests the filter selects, and how many the project has.
  count_listed() { # $1 = project, $2 = filter (may be empty), $3 = file for dotnet's output
    local list_args=(--no-build --list-tests)
    if [ -n "$2" ]; then list_args+=(--filter "$2"); fi
    dotnet test "$1" "${list_args[@]}" >"$3" 2>&1
    grep -cE '^    [^ ]' "$3" || true
  }
  [ -z "${CM_SHARD_REPORT:-}" ] || rm -f "$CM_SHARD_REPORT"
  for lane in "${lanes[@]}"; do
    name="${lane%%:*}"
    proj="${lane#*:}"
    log="$log_dir/list-$name.log"
    listed=$(count_listed "$proj" "$test_filter" "$log")
    if [ "$listed" -eq 0 ]; then
      fail "test $name: CM_TEST_FILTER matches no tests (an empty shard would hide a coverage gap)" "filter: $test_filter"
    fi
    echo "$listed" >"$log_dir/test-$name.listed"
    if [ -n "${CM_SHARD_REPORT:-}" ]; then
      echo "$(count_listed "$proj" "" "$log_dir/list-$name-all.log")" >"$log_dir/test-$name.total"
    fi
  done
fi
test_wall_start=$SECONDS
export CM_PERF_LOG="$log_dir/postgres-perf.log"
rm -f "$CM_PERF_LOG"
pids=()
for lane in "${lanes[@]}"; do
  name="${lane%%:*}"
  proj="${lane#*:}"
  (
    start=$SECONDS
    dotnet test "$proj" "${test_args[@]}" >"$log_dir/test-$name.log" 2>&1
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
  if [ -n "$test_filter" ] && [ "$lpassed" -eq 0 ]; then
    fail "test $name: CM_TEST_FILTER ran no tests (an empty shard would hide a coverage gap)" "filter: $test_filter"
  fi
  ok "test $name: $lpassed passed (${lane_seconds}s)"
  if [ -n "$test_filter" ]; then
    echo "     shard: $(cat "$log_dir/test-$name.listed") of $(cat "$log_dir/test-$name.total" 2>/dev/null || echo '?') listed tests"
    if [ -n "${CM_SHARD_REPORT:-}" ]; then
      mkdir -p "$(dirname "$CM_SHARD_REPORT")"
      echo "$name $(cat "$log_dir/test-$name.listed") $(cat "$log_dir/test-$name.total") $lpassed" >>"$CM_SHARD_REPORT"
    fi
  fi
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
  # The app gate's own logs land here too, so one upload carries app.log and the test.log it points to (#260). On CI the
  # workflow names the folder it uploads; anywhere else a value in the environment is ignored, because one left behind
  # by an earlier run would send every checkout back to a single shared folder.
  if [ "${GITHUB_ACTIONS:-}" != "true" ] || [ -z "${CM_APP_CHECK_LOG_DIR:-}" ]; then
    export CM_APP_CHECK_LOG_DIR="$log_dir/app"
  fi
  run_step app npm run check --prefix app --silent
  if [ "$code" -ne 0 ]; then
    fail "app (npm run check)" "$(grep -v '^[[:space:]]*$' "$log" | tail -n "$max_lines")"
  fi
  ok "app: npm run check (${seconds}s)"
fi

if $full_run; then
  echo "all green ($((SECONDS - script_start))s)"
fi
