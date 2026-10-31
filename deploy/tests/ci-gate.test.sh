#!/usr/bin/env bash
# Tests for scripts/wait-for-ci.sh (the release workflow's CI gate). Prints "PASS name" or "FAIL name: why" per test,
# then a summary line. A fake `gh` (a script first on the PATH) answers from a list of canned lines, one per call; the
# gate's own sleeps are zero. Touches only a temp folder and never the network.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
gate="$here/../../scripts/wait-for-ci.sh"

pass=0
fail=0
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

ok() { pass=$((pass + 1)); echo "PASS $1"; }
bad() { fail=$((fail + 1)); echo "FAIL $1: $2"; }
check() { if [[ "$2" == "$3" ]]; then ok "$1"; else bad "$1" "expected [$2] but got [$3]"; fi; }
contains() { if [[ "$3" == *"$2"* ]]; then ok "$1"; else bad "$1" "expected to find [$2] in: $3"; fi; }

SHA="0123456789abcdef0123456789abcdef01234567"
OTHER="fedcba9876543210fedcba9876543210fedcba98"

mkdir -p "$tmp/bin"
cat >"$tmp/bin/gh" <<'FAKE'
#!/usr/bin/env bash
# Answers call N with line N of $FAKE_GH_FILE (the last line repeats). ERROR = exit 1 with no output, NONE = nothing.
n="$(cat "$FAKE_GH_FILE.n" 2>/dev/null || echo 0)"; n=$((n + 1)); echo "$n" >"$FAKE_GH_FILE.n"
echo "$*" >>"$FAKE_GH_FILE.args"
total="$(wc -l <"$FAKE_GH_FILE" | tr -d ' ')"
((n > total)) && n="$total"
line="$(sed -n "${n}p" "$FAKE_GH_FILE")"
[[ "$line" == ERROR ]] && exit 1
[[ "$line" == NONE ]] && exit 0
printf '%s\n' "$line"
FAKE
cat >"$tmp/bin/sleep" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >>"$FAKE_GH_FILE.sleeps"
FAKE
chmod +x "$tmp/bin/gh" "$tmp/bin/sleep"

url="https://github.com/o/r/actions/runs/1"
# run_gate "line1\nline2..." [env assignments...]: runs the gate with those canned answers; sets out, rc, calls.
run_gate() {
  local lines="$1"; shift
  rm -f "$tmp/gh.lines" "$tmp/gh.lines.n" "$tmp/gh.lines.args" "$tmp/gh.lines.sleeps" "$tmp/output"
  printf '%b\n' "$lines" >"$tmp/gh.lines"
  out="$(env PATH="$tmp/bin:$PATH" FAKE_GH_FILE="$tmp/gh.lines" GH_REPO="o/r" SHA="$SHA" GITHUB_OUTPUT="$tmp/output" \
    CI_POLL_SECONDS=1 CI_APPEAR_SECONDS=3 CI_WAIT_SECONDS=6 "$@" bash "$gate" 2>&1)"
  rc=$?
  calls="$(cat "$tmp/gh.lines.n" 2>/dev/null || echo 0)"
}
passed_output() { cat "$tmp/output" 2>/dev/null || echo "(none)"; }

# ---- the verdicts ---------------------------------------------------------------------------------------------------
run_gate "completed|success|$url"
check "gate: CI already green for the commit passes" "0" "$rc"
check "gate: ...and says passed=true" "passed=true" "$(passed_output)"
check "gate: ...after a single look" "1" "$calls"

run_gate "in_progress|none|$url\nin_progress|none|$url\ncompleted|success|$url"
check "gate: it waits while CI runs, then passes" "0" "$rc"
check "gate: ...looking again each time" "3" "$calls"
check "gate: ...sleeping the poll interval between looks" "1 1" "$(tr '\n' ' ' <"$tmp/gh.lines.sleeps" | sed 's/ $//')"

run_gate "queued|none|$url\ncompleted|success|$url"
check "gate: a queued run is waited for" "0" "$rc"

run_gate "completed|failure|$url"
check "gate: red CI holds the release back (exit 1)" "1" "$rc"
contains "gate: ...and says CI failed" "CI ended 'failure'" "$out"
check "gate: ...without writing passed=true" "(none)" "$(passed_output)"

run_gate "in_progress|none|$url\ncompleted|failure|$url"
check "gate: CI that turns red while waited on holds the release back" "1" "$rc"

run_gate "completed|timed_out|$url"
check "gate: a timed-out CI holds the release back" "1" "$rc"
run_gate "completed|action_required|$url"
check "gate: any other conclusion holds the release back" "1" "$rc"
run_gate "completed|skipped|$url"
check "gate: a skipped CI is not a pass" "1" "$rc"
run_gate "completed|none|$url"
check "gate: a finished run with no conclusion is not a pass" "1" "$rc"

run_gate "completed|cancelled|$url"
check "gate: CI cancelled for the commit (a newer push replaced it) is not an error" "0" "$rc"
check "gate: ...but nothing is released: passed=false" "passed=false" "$(passed_output)"

# ---- a run that is not there yet, and a GitHub that cannot be read ---------------------------------------------------
run_gate "NONE\nNONE\ncompleted|success|$url"
check "gate: it waits for the CI run to appear" "0" "$rc"
check "gate: ...looking again" "3" "$calls"

run_gate "NONE"
check "gate: no CI run ever appears: the release is held back" "1" "$rc"
contains "gate: ...and says so" "No ci.yml run for 0123456789ab" "$out"
check "gate: ...after the appear timeout (3 s at 1 s a look = 3 looks)" "3" "$calls"

run_gate "ERROR"
check "gate: GitHub unreadable for the whole appear window holds the release back" "1" "$rc"

run_gate "ERROR\nERROR\ncompleted|success|$url"
check "gate: a couple of failed reads are retried" "0" "$rc"

run_gate "in_progress|none|$url\nERROR\nERROR\nERROR\nERROR\nERROR\nERROR\nERROR"
check "gate: unreadable after CI was seen running still ends (the wait timeout), fail closed" "1" "$rc"
contains "gate: ...as a timeout" "did not finish in time" "$out"

run_gate "in_progress|none|$url"
check "gate: CI that never finishes times out and holds the release back" "1" "$rc"
check "gate: ...after the wait timeout (6 s at 1 s a look = 6 looks)" "6" "$calls"
contains "gate: ...and says so" "did not finish in time" "$out"

# ---- it asks about exactly this commit -------------------------------------------------------------------------------
run_gate "completed|success|$url"
args="$(cat "$tmp/gh.lines.args")"
contains "gate: it reads the runs of ci.yml" "repos/o/r/actions/workflows/ci.yml/runs" "$args"
contains "gate: ...for this commit" "head_sha=$SHA" "$args"
contains "gate: ...from a push, on main" "event=push&branch=main" "$args"
contains "gate: ...and keeps only the runs whose head_sha is this commit (so another commit's CI can never vouch)" "select(.head_sha == \"$SHA\")" "$args"
check "gate: it never asks about a different commit" "0" "$(grep -c "$OTHER" "$tmp/gh.lines.args")"

run_gate "completed|success|$url" CI_WORKFLOW=other.yml CI_BRANCH=dev
contains "gate: the workflow file and branch can be changed" "workflows/other.yml/runs?head_sha=$SHA&event=push&branch=dev" "$(cat "$tmp/gh.lines.args")"

# ---- bad input ---------------------------------------------------------------------------------------------------------
run_gate "completed|success|$url" SHA="main"
check "gate: a SHA that is not 40 hex digits is refused (it goes into a jq filter)" "1" "$rc"
check "gate: ...before asking GitHub anything" "0" "$calls"
run_gate "completed|success|$url" "SHA=${SHA}\" or true or \""
check "gate: a SHA with quote characters is refused" "1" "$rc"
run_gate "completed|success|$url" SHA=""
check "gate: an empty SHA is refused" "1" "$rc"
out="$(env -u GH_REPO PATH="$tmp/bin:$PATH" FAKE_GH_FILE="$tmp/gh.lines" SHA="$SHA" bash "$gate" 2>&1)"; rc=$?
check "gate: a missing GH_REPO is refused" "1" "$rc"

# ---- no GITHUB_OUTPUT is fine --------------------------------------------------------------------------------------------
printf 'completed|success|%s\n' "$url" >"$tmp/gh.lines"; rm -f "$tmp/gh.lines.n"
out="$(env -u GITHUB_OUTPUT PATH="$tmp/bin:$PATH" FAKE_GH_FILE="$tmp/gh.lines" GH_REPO=o/r SHA="$SHA" bash "$gate" 2>&1)"; rc=$?
check "gate: it runs without GITHUB_OUTPUT (locally)" "0" "$rc"

echo "SUMMARY passed=$pass failed=$fail"
((fail == 0))
