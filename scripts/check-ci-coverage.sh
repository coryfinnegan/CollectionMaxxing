#!/usr/bin/env bash
# Fails when a test project under tests/ is not run by any shard of the CI workflow (or a shard names a project that
# does not exist).
#
# Why: scripts/check.sh runs every tests/*/*.csproj, so a new test project passes locally; but ci.yml lists the projects
# of each backend-test shard by name (the `projects:` of the matrix, which becomes CM_TEST_ONLY). A project missing from
# every shard is silently never run in CI, and the `backend` check stays green. A name that matches nothing is just as
# quiet when it sits beside a valid one (check.sh only fails when NONE of the names match).
#
# What counts as a test project: every project under tests/ except the support libraries. The libraries are the ones
# tests/Directory.Build.targets leaves out of the xUnit packages (its `MSBuildProjectName != '...'` conditions): that is
# what makes a project a test project in the build, so the list is read from there, not copied.
#
# Usage:  bash scripts/check-ci-coverage.sh [repo-root]        (CI runs it in the backend-tooling job)
set -u

root="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
targets="$root/tests/Directory.Build.targets"
workflow="$root/.github/workflows/ci.yml"

problems=()
[[ -f "$targets" ]] || { echo "ci coverage: $targets not found"; exit 2; }
[[ -f "$workflow" ]] || { echo "ci coverage: $workflow not found"; exit 2; }

# The support libraries: every name in a `MSBuildProjectName)' != 'Name'` condition of tests/Directory.Build.targets.
libraries="$(grep -oE "MSBuildProjectName\)' *!= *'[A-Za-z0-9_.]+'" "$targets" | sed -E "s/.*!= *'([^']+)'/\1/" | sort -u)"
if [[ -z "$libraries" ]]; then
  echo "ci coverage: found no support libraries in tests/Directory.Build.targets (no MSBuildProjectName != '...' condition)."
  echo "             If that file changed how it tells libraries from test projects, update this check to match."
  exit 2
fi

# Every project under tests/ (one level down, as check.sh finds them), split into test projects and libraries.
projects=""
tests=""
for proj in "$root"/tests/*/*.csproj; do
  [[ -f "$proj" ]] || continue
  name="$(basename "$proj" .csproj)"
  projects+="$name"$'\n'
  grep -qxF -- "$name" <<<"$libraries" || tests+="$name"$'\n'
done
tests="$(printf '%s' "$tests" | sort -u)"
projects="$(printf '%s' "$projects" | sort -u)"
if [[ -z "$tests" ]]; then
  echo "ci coverage: found no test projects under tests/"
  exit 2
fi

# The `projects:` values of the CI matrix: a one-line value, or a folded/literal block (`>-`, `|`) of indented lines.
# Prints one project name per line, and "SHARD-WITHOUT-PROJECTS name" for a shard entry that lists none.
shard_report="$(awk '
  function indent_of(s) { match(s, /[^ ]/); return RSTART - 1 }
  /^[[:space:]]*#/ { next }
  /^[[:space:]]*-[[:space:]]+shard:/ {
    if (shard != "" && !has) print "SHARD-WITHOUT-PROJECTS " shard
    shard = $0; sub(/^[[:space:]]*-[[:space:]]+shard:[[:space:]]*/, "", shard); has = 0; block = 0; next
  }
  /^[[:space:]]+projects:/ {
    value = $0; sub(/^[[:space:]]+projects:[[:space:]]*/, "", value); sub(/[[:space:]]+#.*$/, "", value)
    if (value ~ /^[>|][-+]?$/) { block = 1; block_indent = indent_of($0); has = 1; next }
    has = 1; block = 0; n = split(value, parts, /[[:space:]]+/)
    for (i = 1; i <= n; i++) if (parts[i] != "") print parts[i]
    next
  }
  block {
    if ($0 ~ /^[[:space:]]*$/) next
    if (indent_of($0) > block_indent) { n = split($0, parts, /[[:space:]]+/); for (i = 1; i <= n; i++) if (parts[i] != "") print parts[i]; next }
    block = 0
  }
  END { if (shard != "" && !has) print "SHARD-WITHOUT-PROJECTS " shard }
' "$workflow")"

while IFS= read -r line; do
  [[ "$line" == SHARD-WITHOUT-PROJECTS* ]] || continue
  problems+=("the ci.yml shard '${line#SHARD-WITHOUT-PROJECTS }' lists no projects (a shard with no CM_TEST_ONLY would run every project, or none; name its projects)")
done <<<"$shard_report"

covered="$(printf '%s\n' "$shard_report" | grep -v '^SHARD-WITHOUT-PROJECTS' | grep . | sort -u || true)"
if [[ -z "$covered" ]]; then
  problems+=("ci.yml names no test projects at all (no shard has a 'projects:' value)")
fi

# A test project that no shard runs.
while IFS= read -r name; do
  [[ -z "$name" ]] && continue
  grep -qxF -- "$name" <<<"$covered" || problems+=("tests/$name is not run by any ci.yml shard: add it to the 'projects:' of a backend-test shard")
done <<<"$tests"

# A name in ci.yml that is not a test project (a typo, a deleted project, or a support library).
while IFS= read -r name; do
  [[ -z "$name" ]] && continue
  grep -qxF -- "$name" <<<"$tests" && continue
  if grep -qxF -- "$name" <<<"$libraries"; then
    problems+=("ci.yml runs '$name', which is a support library, not a test project")
  else
    problems+=("ci.yml names '$name', but there is no tests/*/$name.csproj (a typo or a deleted project; check.sh would skip it silently)")
  fi
done <<<"$covered"

if ((${#problems[@]} > 0)); then
  echo "ci coverage: FAILED"
  printf '  - %s\n' "${problems[@]}"
  exit 1
fi
echo "ci coverage: ok, all $(printf '%s\n' "$tests" | grep -c .) test projects are run by a ci.yml shard ($(printf '%s\n' "$libraries" | grep -c .) support libraries skipped)"
