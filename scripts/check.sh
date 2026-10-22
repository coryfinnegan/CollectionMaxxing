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
# While it runs, prints a line every 30s (what is running, for how long, and the step's own progress note) so a long
# step never looks hung. A step may define progress_note to say more; it is cleared again afterwards.
run_step() {
  local name="$1"; shift
  log="$log_dir/$name.log"
  local start=$SECONDS
  "$@" >"$log" 2>&1 &
  local pid=$!
  while kill -0 "$pid" 2>/dev/null; do
    local tick
    for tick in $(seq 15); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 2
    done
    if kill -0 "$pid" 2>/dev/null; then
      echo "     ... $name running, $((SECONDS - start))s$(progress_note 2>/dev/null)"
    fi
  done
  wait "$pid"
  code=$?
  seconds=$((SECONDS - start))
  progress_note() { :; }
}

progress_note() { :; }

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
# Normal console verbosity logs every test as it finishes (with its time), so the heartbeat can say how far along it is;
# the trx files give the slowest-tests report below. Everything goes to the log file, the terminal stays quiet.
trx_dir="$log_dir/trx"
rm -rf "$trx_dir"
progress_note() {
  local done_count
  done_count=$(grep -cE '^[[:space:]]+(Passed|Failed|Skipped) ' "$log" 2>/dev/null)
  echo ", ${done_count:-0} tests finished, projects done: $(grep -cE '^(Passed|Failed)!' "$log" 2>/dev/null)"
}
run_step test dotnet test "$solution" --no-build --logger 'console;verbosity=normal' --logger 'trx;LogFilePrefix=check' --results-directory "$trx_dir"
# Normal verbosity prints a "Test Run Successful / Total tests / Passed: N" block per project (Failed and Skipped only when
# non-zero); minimal prints "Passed! - Failed: ..." lines. Count whichever is there.
read -r failed passed skipped < <(
  awk '
    /^(Passed|Failed)!/ && match($0, /Failed:[[:space:]]+[0-9]+/) { f += substr($0, RSTART + 7) + 0
      match($0, /Passed:[[:space:]]+[0-9]+/); p += substr($0, RSTART + 7) + 0
      match($0, /Skipped:[[:space:]]+[0-9]+/); s += substr($0, RSTART + 8) + 0; next }
    /^[[:space:]]+Passed:[[:space:]]+[0-9]+[[:space:]]*$/ { p += $2 }
    /^[[:space:]]+Failed:[[:space:]]+[0-9]+[[:space:]]*$/ { f += $2 }
    /^[[:space:]]+Skipped:[[:space:]]+[0-9]+[[:space:]]*$/ { s += $2 }
    END { print f + 0, p + 0, s + 0 }' "$log"
)
# Where the time went: one line per test project, a histogram of test times, then the slowest classes and tests.
sed -nE 's/^.*xUnit\.net ([0-9:.]+)\][[:space:]]+Finished:[[:space:]]+([A-Za-z.]+) .*/     \2: \1/p' "$log"
report=$(cat "$trx_dir"/*.trx 2>/dev/null | awk '
  match($0, /testName="[^"]*"/) { name = substr($0, RSTART + 10, RLENGTH - 11) }
  match($0, /duration="[0-9:.]*"/) {
    split(substr($0, RSTART + 10, RLENGTH - 11), t, ":")
    secs = t[1] * 3600 + t[2] * 60 + t[3]
    sub(/\(.*$/, "", name)
    cls = name; sub(/\.[^.]*$/, "", cls)
    total += secs; count++
    class_secs[cls] += secs; class_count[cls]++
    bucket = secs < 0.1 ? "under_0.1s" : secs < 0.5 ? "0.1-0.5s" : secs < 1 ? "0.5-1s" : secs < 2 ? "1-2s" : "2s_and_over"
    buckets[bucket]++; bucket_secs[bucket] += secs
    if (secs >= 2) printf "T %8.1fs  %s\n", secs, name
  }
  END {
    printf "H %d tests, %.0fs of test time in total\n", count, total
    for (b in buckets) printf "B %-12s %5d tests %7.0fs\n", b, buckets[b], bucket_secs[b]
    for (c in class_secs) printf "C %8.1fs %5d tests  %s\n", class_secs[c], class_count[c], c
  }')
echo "     $(printf '%s\n' "$report" | sed -n 's/^H //p')"
printf '%s\n' "$report" | sed -n 's/^B /       /p' | sort
echo "     slowest classes (total time):"
printf '%s\n' "$report" | sed -n 's/^C /       /p' | sort -rn | head -n 12
echo "     slowest tests:"
printf '%s\n' "$report" | sed -n 's/^T /       /p' | sort -rn | head -n 10
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
