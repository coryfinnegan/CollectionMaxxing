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
# Run from anywhere:  bash scripts/check.sh [--changed] [phase ...]
# Phases are restore, build, format, test and app; with no arguments all of them run. CI runs one phase per step so
# timings and failures localize (the backend jobs skip the app phase; the app job owns npm run check).
#
# --changed narrows the gate to what the change can affect (the same classification as CI's changes job, scripts/ci/changed-areas.sh). The changed
# files are `git diff origin/main...HEAD` plus uncommitted and untracked files. Only paths under app/ (except
# app/playwright/serve.mjs, which the backend tests read) run just the app phase. Only paths under src/, tests/, deploy/ and
# evals/ skip the app phase, and run the test phase only for the test projects the change affects: a test project whose own
# folder changed, or one that references (through ProjectReference, transitively) a project whose folder changed, plus the
# Architecture tests, which read the source and test folders as a whole. Format runs only on projects whose folder changed.
# A shared file (tests/Directory.Build.props, a file in src/ or tests/ outside any project), an empty change, a change
# outside those folders, or a missing origin/main runs everything. One `changed:` line says what ran and what was skipped.
# Without --changed the gate behaves as it always did. CI never passes --changed: it is the full test of record.
#
# CM_FORMAT_PARALLEL sets how many `dotnet format` runs go at once (default: the CPU count, at least 2 and at most 4).
#
# CM_TEST_ONLY limits the test phase to the named test projects (whitespace-separated basenames, e.g.
# CM_TEST_ONLY="CollectionMaxxing.Domain.Tests"). CI's backend-test matrix sets it so each shard runs its own
# projects; unset, every test project runs. All the usual guards still apply to the lanes that run.
#
# CM_TEST_FILTER hands a VSTest filter expression (`dotnet test --filter`; xUnit v3 runs in its VSTest flavour here,
# see tests/Directory.Build.targets) to every selected project; CI uses it with CM_TEST_ONLY to shard the three slow Api
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

# The phases to run. No phase arguments = the whole gate. --changed is a flag, not a phase.
changed_only=false
phase_args=()
for arg in "$@"; do
  case "$arg" in
    --changed) changed_only=true;;
    *) phase_args+=("$arg");;
  esac
done
full_run=true
phases=(restore build format test app)
if [ "${#phase_args[@]}" -gt 0 ]; then
  phases=("${phase_args[@]}")
  full_run=false
fi
for phase in "${phases[@]}"; do
  case "$phase" in
    restore|build|format|test|app) ;;
    *) echo "unknown phase '$phase' (want: restore build format test app)" >&2; exit 2;;
  esac
done

# ---- --changed: which part of the gate a change can affect ---------------------------------------------------------------
# Every tests/ project except these two support libraries is a test project (see tests/Directory.Build.targets).
is_support_lib() { case "$1" in CollectionMaxxing.TestSupport|CollectionMaxxing.Api.TestKit) return 0;; *) return 1;; esac; }

# The solution's projects as "name|dir" lines (dir is src/<folder> or tests/<folder>); a reference is matched by file
# name, which is how this repo names projects (one folder, one csproj of the same name).
list_projects() {
  local f n
  for f in src/*/*.csproj tests/*/*.csproj; do
    [ -f "$f" ] || continue
    n="${f##*/}"
    echo "${n%.csproj}|${f%/*}"
  done
}

# One "name:ref ref ..." line per project: the names of the projects it references directly. One awk run for all of them,
# because a process per project is slow on Windows.
project_refs_table() {
  awk '
    FNR == 1 { if (NR > 1) printf "\n"; n = FILENAME; sub(/^.*\//, "", n); sub(/\.csproj$/, "", n); printf "%s:", n }
    { line = $0
      while (match(line, /ProjectReference Include="[^"]*"/)) {
        r = substr(line, RSTART, RLENGTH); sub(/^ProjectReference Include="/, "", r); sub(/"$/, "", r)
        gsub(/\\/, "/", r); sub(/^.*\//, "", r); sub(/\.csproj$/, "", r)
        printf " %s", r
        line = substr(line, RSTART + RLENGTH)
      } }
    END { printf "\n" }
  ' src/*/*.csproj tests/*/*.csproj
}

# The project itself and everything it references, transitively, as a space separated $closure_result. Reads $refs_table.
closure_of() { # $1 = project name
  local seen=" " queue="$1" cur row r
  while [ -n "$queue" ]; do
    cur="${queue%% *}"
    if [ "$cur" = "$queue" ]; then queue=""; else queue="${queue#* }"; fi
    case "$seen" in *" $cur "*) continue;; esac
    seen="$seen$cur "
    while IFS= read -r row; do
      case "$row" in "$cur:"*) for r in ${row#*:}; do queue="$queue${queue:+ }$r"; done; break;; esac
    done <<<"$refs_table"
  done
  closure_result="$seen"   # a variable, not output: a command substitution is a process, and those are slow on Windows
}

# The changed paths, NUL separated (relative to the repo root): committed on this branch since it left origin/main, plus
# staged, unstaged and untracked files. Fails when that cannot be worked out. -z keeps odd file names unquoted, and
# --no-renames lists both ends of a move, so a file moved out of src/ still counts as a src/ change.
changed_paths() {
  git rev-parse --verify --quiet 'origin/main^{commit}' >/dev/null 2>&1 || return 1
  git diff --name-only --no-renames -z origin/main...HEAD 2>/dev/null || return 1
  git diff --name-only --no-renames -z HEAD 2>/dev/null || return 1
  git ls-files --others --exclude-standard -z 2>/dev/null || return 1
}

short_names() { # names on stdin or as arguments -> "A, B" without the CollectionMaxxing. prefix, sorted
  local n
  for n in "$@"; do echo "${n#CollectionMaxxing.}"; done | LC_ALL=C sort | awk 'BEGIN { s = "" } { s = s (NR > 1 ? ", " : "") $0 } END { print s }'
}

# Sets the scope_* variables: scope_area (app | server | all), scope_backend and scope_app (true | false), scope_test_all
# and scope_format_all (true | false), scope_tests and scope_format (space separated names, when not "all"), scope_line.
compute_scope() {
  scope_area=all; scope_backend=true; scope_app=true; scope_test_all=true; scope_format_all=true; scope_tests=""; scope_format=""
  local paths=() p pname pdir
  if ! changed_paths >"$log_dir/changed-paths.z"; then
    scope_line="changed: everything. skipped: nothing (cannot tell what changed: no origin/main to compare with)"
    return 0
  fi
  while IFS= read -r p; do
    if [ -n "$p" ]; then paths+=("$p"); fi
  done < <(tr '\0' '\n' <"$log_dir/changed-paths.z" | LC_ALL=C sort -u)
  if [ "${#paths[@]}" -eq 0 ]; then
    scope_line="changed: everything. skipped: nothing (no changed files found)"
    return 0
  fi
  local count="${#paths[@]}" files app_only=true server_only=true outside="" has_serve=false
  files="$count file"; [ "$count" -eq 1 ] || files="${files}s"
  for p in "${paths[@]}"; do
    case "$p" in
      app/playwright/serve.mjs) app_only=false; has_serve=true;;
      app/*) ;;
      *) app_only=false;;
    esac
    case "$p" in
      src/*|tests/*|deploy/*|evals/*) ;;
      *) server_only=false;;
    esac
    case "$p" in
      app/*|src/*|tests/*|deploy/*|evals/*) ;;
      *) [ -n "$outside" ] || outside="$p";;
    esac
  done
  if $app_only; then
    scope_area=app; scope_backend=false
    scope_line="changed: app-only, $files. skipped: restore, build, format and test (nothing outside app/ changed)"
    return 0
  fi
  if ! $server_only; then
    local why="the change touches both the app and the server"
    if [ -n "$outside" ]; then why="$outside is outside app/ and the server folders"
    elif $has_serve; then why="app/playwright/serve.mjs is read by the backend tests"; fi
    scope_line="changed: everything, $files. skipped: nothing ($why)"
    return 0
  fi

  # Server-only: work out which projects the change touches.
  scope_area=server; scope_app=false; scope_test_all=false; scope_format_all=false
  local projects changed_projects=" " touched_for_tests=" " shared="" touches_code=false name
  projects="$(list_projects)"
  refs_table="$(project_refs_table)"
  for p in "${paths[@]}"; do
    case "$p" in
      src/*|tests/*)
        touches_code=true
        name=""
        while IFS='|' read -r pname pdir; do
          case "$p" in "$pdir"/*) name="$pname"; break;; esac
        done <<<"$projects"
        if [ -n "$name" ]; then changed_projects="$changed_projects$name "
        elif [ -z "$shared" ]; then shared="$p"; fi
        ;;
      evals/*)
        # The recognition eval tests read evals/ (the baseline, the acceptance truth, the results). Nothing there needs formatting.
        if [ -d tests/CollectionMaxxing.Recognition.Evals ]; then touched_for_tests="${touched_for_tests}CollectionMaxxing.Recognition.Evals "; fi
        ;;
    esac
  done
  touched_for_tests="$touched_for_tests$changed_projects"
  local lanes=() all_projects=() skipped="app phase (nothing under app/ changed)" kept=""
  while IFS='|' read -r pname pdir; do
    all_projects+=("$pname")
    case "$pdir" in tests/*) is_support_lib "$pname" || lanes+=("$pname");; esac
  done <<<"$projects"
  if [ -n "$shared" ]; then
    # A file in src/ or tests/ that no project owns (tests/Directory.Build.props, a shared props file): it can reach any project.
    scope_test_all=true; scope_format_all=true
    kept=" Everything else runs: $shared is shared by the projects."
  else
    local always="" lane member hit
    if $touches_code; then always="CollectionMaxxing.Architecture.Tests"; fi
    for lane in "${lanes[@]}"; do
      hit=false
      [ "$lane" = "$always" ] && hit=true
      if ! $hit; then
        closure_of "$lane"
        for member in $closure_result; do
          case "$touched_for_tests" in *" $member "*) hit=true; break;; esac
        done
      fi
      $hit && scope_tests="$scope_tests${scope_tests:+ }$lane"
    done
    for name in "${all_projects[@]}"; do
      case "$changed_projects" in *" $name "*) scope_format="$scope_format${scope_format:+ }$name";; esac
    done
    local n_tests=0 n_format=0
    [ -z "$scope_tests" ] || n_tests="$(set -- $scope_tests; echo $#)"
    [ -z "$scope_format" ] || n_format="$(set -- $scope_format; echo $#)"
    if [ "$n_tests" -eq 0 ]; then skipped="$skipped; all ${#lanes[@]} test projects (none is affected)"
    elif [ "$n_tests" -lt "${#lanes[@]}" ]; then skipped="$skipped; $(( ${#lanes[@]} - n_tests )) of ${#lanes[@]} test projects (not affected)"; fi
    if [ "$n_format" -eq 0 ]; then skipped="$skipped; format of all ${#all_projects[@]} projects (none changed)"
    elif [ "$n_format" -lt "${#all_projects[@]}" ]; then skipped="$skipped; format of $(( ${#all_projects[@]} - n_format )) of ${#all_projects[@]} projects (unchanged)"; fi
    [ "$n_tests" -eq 0 ] || kept="$kept testing: $(short_names $scope_tests)."
    [ "$n_format" -eq 0 ] || kept="$kept formatting: $(short_names $scope_format)."
  fi
  scope_line="changed: server-only, $files. skipped: $skipped.$kept"
}

# With --changed, the scope says which phases a change can affect; without it every phase wants to run.
scope_backend=true; scope_app=true; scope_test_all=true; scope_format_all=true; scope_tests=""; scope_format=""; scope_area=""
if $changed_only; then
  compute_scope
  echo "$scope_line"
fi
want() {
  local p="$1" q found=false
  for q in "${phases[@]}"; do [ "$q" = "$p" ] && found=true; done
  $found || return 1
  if [ "$p" = app ]; then $scope_app; else $scope_backend; fi
}

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
# Projects run side by side (CM_FORMAT_PARALLEL at a time, default the CPU count clamped to 2..4): each is its own
# dotnet process, so they do not share state, and the log below is written in project order afterwards.
format_projects=()
for proj in src/*/*.csproj tests/*/*.csproj; do
  [ -f "$proj" ] || continue
  if $scope_format_all; then
    format_projects+=("$proj")
  else
    case " $scope_format " in *" $(basename "$proj" .csproj) "*) format_projects+=("$proj");; esac
  fi
done
format_parallel="${CM_FORMAT_PARALLEL:-}"
case "$format_parallel" in
  ''|*[!0-9]*|0)
    format_parallel="$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 2)"
    [ "$format_parallel" -ge 2 ] 2>/dev/null || format_parallel=2
    [ "$format_parallel" -le 4 ] || format_parallel=4
    ;;
esac
rm -rf "$log_dir/format-parts"
mkdir -p "$log_dir/format-parts"
format_one() { # $1 = project; leaves <name>.log, <name>.retries and, when it verified, <name>.ok in format-parts
  local proj="$1" name part attempt=0 verified=false code
  name="$(basename "$proj" .csproj)"
  part="$log_dir/format-parts/$name"
  : >"$part.log"
  : >"$part.retries"
  while [ "$verified" = false ] && [ "$attempt" -lt "$max_attempts" ]; do
    attempt=$((attempt + 1))
    dotnet format "$proj" --verify-no-changes --no-restore >"$part.attempt" 2>&1
    code=$?
    cat "$part.attempt" >>"$part.log"
    if [ "$code" -eq 0 ]; then
      verified=true
    elif grep -q 'Unhandled exception' "$part.attempt"; then
      if [ "$attempt" -lt "$max_attempts" ]; then
        echo "hit the Roslyn crash, retrying (attempt $((attempt + 1))/$max_attempts; see issue #308)" >>"$part.retries"
      fi
    elif grep -q 'being used by another process' "$part.attempt"; then
      # Two projects that share a referenced project can both write its obj/ files while they load it (nothing is built
      # before the format phase on CI). Only that error is retried, so a genuine violation still fails at once.
      if [ "$attempt" -lt "$max_attempts" ]; then
        echo "hit a file-in-use error from a run beside it, retrying (attempt $((attempt + 1))/$max_attempts)" >>"$part.retries"
      fi
    else
      break
    fi
  done
  if [ "$verified" = true ]; then : >"$part.ok"; fi
  return 0
}
export -f format_one
export log_dir max_attempts
if [ "${#format_projects[@]}" -gt 0 ]; then
  printf '%s\0' "${format_projects[@]}" | xargs -0 -n 1 -P "$format_parallel" bash -c 'format_one "$1"' _
  for proj in "${format_projects[@]}"; do
    name="$(basename "$proj" .csproj)"
    cat "$log_dir/format-parts/$name.log" >>"$log" 2>/dev/null
    while IFS= read -r retry; do
      echo "     format: $name $retry"
    done <"$log_dir/format-parts/$name.retries"
    [ -f "$log_dir/format-parts/$name.ok" ] || failed="$failed${failed:+, }$name"
  done
fi
seconds=$((SECONDS - start))
if [ -n "$failed" ]; then
  details="$(grep ': error ' "$log" | sed -E 's/[[:space:]]*\[[^]]*\][[:space:]]*$//' | awk '!seen[$0]++' | head -n "$max_lines")"
  [ -n "$details" ] || details="$(grep -v '^[[:space:]]*$' "$log" | tail -n "$max_lines")"
  fail "format ($failed; fix with: dotnet format <project>.csproj)" "$details"
fi
ok "format (${#format_projects[@]} projects, ${seconds}s)"

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
skip_tests=false
if want test && ! $scope_test_all && [ -z "$scope_tests" ]; then
  skip_tests=true
  ok "test: skipped, no test project is affected by this change"
fi
if want test && ! $skip_tests; then
# Every tests/ project except the two support libraries (see tests/Directory.Build.targets), each in its own process at
# the same time. A project that needs Postgres starts its own container, so lanes never see each other's data.
lanes=()
for proj in tests/*/*.csproj; do
  name="$(basename "$proj" .csproj)"
  is_support_lib "$name" && continue
  # --changed: only the test projects the change affects (the scope line printed above names them).
  if ! $scope_test_all; then
    case " $scope_tests " in *" $name "*) ;; *) continue;; esac
  fi
  lanes+=("$name:$proj")
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
  scope_note=""
  case "$scope_area" in app|server) scope_note=", changed files only";; esac
  echo "all green ($((SECONDS - script_start))s$scope_note)"
fi
