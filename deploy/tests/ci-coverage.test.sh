#!/usr/bin/env bash
# Tests for scripts/check-ci-coverage.sh (every test project must be run by a ci.yml shard). Prints "PASS name" or
# "FAIL name: why" per test, then a summary line. Builds small fake repositories in a temp folder; the last tests run
# the check on this repository itself.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
check_script="$here/../../scripts/check-ci-coverage.sh"

pass=0
fail=0
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

ok() { pass=$((pass + 1)); echo "PASS $1"; }
bad() { fail=$((fail + 1)); echo "FAIL $1: $2"; }
check() { if [[ "$2" == "$3" ]]; then ok "$1"; else bad "$1" "expected [$2] but got [$3]"; fi; }
contains() { if [[ "$3" == *"$2"* ]]; then ok "$1"; else bad "$1" "expected to find [$2] in: $3"; fi; }

# A fake repo: tests/<name>/<name>.csproj for each project, the targets file that names two libraries, and a ci.yml
# whose matrix is given as text. $1 = folder, $2 = project names (space separated), $3 = the matrix `include:` text.
make_repo() {
  local dir="$1" names="$2" matrix="$3" n
  rm -rf "$dir"; mkdir -p "$dir/tests" "$dir/.github/workflows"
  for n in $names; do mkdir -p "$dir/tests/$n"; echo '<Project />' >"$dir/tests/$n/$n.csproj"; done
  cat >"$dir/tests/Directory.Build.targets" <<'XML'
<Project>
  <ItemGroup Condition="'$(MSBuildProjectName)' != 'Fake.TestSupport' And '$(MSBuildProjectName)' != 'Fake.Api.TestKit'">
    <PackageReference Include="xunit.v3.mtp-off" />
  </ItemGroup>
</Project>
XML
  cat >"$dir/.github/workflows/ci.yml" <<YAML
name: CI
jobs:
  backend-test:
    strategy:
      matrix:
        include:
$matrix
    steps:
      - run: echo hi
YAML
}
run_check() { out="$(bash "$check_script" "$1" 2>&1)"; rc=$?; }

all="Fake.Api.Tests Fake.Domain.Tests Fake.Infra.Tests Fake.TestSupport Fake.Api.TestKit"

# ---- covered ---------------------------------------------------------------------------------------------------------
matrix_ok='          - shard: api-1
            projects: Fake.Api.Tests
            filter: >-
              FullyQualifiedName~Fake.Api.Tests.One.
          - shard: api-2
            projects: Fake.Api.Tests
            filter: >-
              FullyQualifiedName!~Fake.Api.Tests.One.
          - shard: rest
            projects: >-
              Fake.Domain.Tests
              Fake.Infra.Tests'
make_repo "$tmp/ok" "$all" "$matrix_ok"
run_check "$tmp/ok"
check "coverage: every test project in a shard passes" "0" "$rc"
contains "coverage: ...and says how many (3 test projects, 2 libraries skipped)" "all 3 test projects are run by a ci.yml shard (2 support libraries skipped)" "$out"

matrix_oneline='          - shard: all
            projects: Fake.Api.Tests Fake.Domain.Tests Fake.Infra.Tests'
make_repo "$tmp/oneline" "$all" "$matrix_oneline"
run_check "$tmp/oneline"
check "coverage: a one-line projects value works too" "0" "$rc"

matrix_literal='          - shard: all
            projects: |
              Fake.Api.Tests
              Fake.Domain.Tests

              Fake.Infra.Tests
            filter: ""'
make_repo "$tmp/literal" "$all" "$matrix_literal"
run_check "$tmp/literal"
check "coverage: a literal block with a blank line works, and the next key ends it" "0" "$rc"

# ---- a project left out ------------------------------------------------------------------------------------------------
make_repo "$tmp/missing" "$all Fake.New.Tests" "$matrix_ok"
run_check "$tmp/missing"
check "coverage: a new test project that no shard names fails" "1" "$rc"
contains "coverage: ...and the message names it" "tests/Fake.New.Tests is not run by any ci.yml shard" "$out"
check "coverage: ...and only it" "1" "$(printf '%s\n' "$out" | grep -c '^  - ')"

make_repo "$tmp/missing2" "$all Fake.New.Tests Fake.Other.Tests" "$matrix_ok"
run_check "$tmp/missing2"
check "coverage: two left out are both named" "2" "$(printf '%s\n' "$out" | grep -c 'is not run by any')"

# ---- a name that matches nothing ----------------------------------------------------------------------------------------
matrix_typo='          - shard: rest
            projects: >-
              Fake.Api.Tests
              Fake.Domain.Tests
              Fake.Infra.Test'
make_repo "$tmp/typo" "$all" "$matrix_typo"
run_check "$tmp/typo"
check "coverage: a misspelled name fails (check.sh would skip it silently beside valid ones)" "1" "$rc"
contains "coverage: ...naming the typo" "names 'Fake.Infra.Test', but there is no tests/*/Fake.Infra.Test.csproj" "$out"
contains "coverage: ...and the project that is now uncovered" "tests/Fake.Infra.Tests is not run" "$out"

matrix_lib='          - shard: rest
            projects: >-
              Fake.Api.Tests Fake.Domain.Tests Fake.Infra.Tests Fake.TestSupport'
make_repo "$tmp/lib" "$all" "$matrix_lib"
run_check "$tmp/lib"
check "coverage: listing a support library as a test project fails" "1" "$rc"
contains "coverage: ...saying it is a library" "Fake.TestSupport', which is a support library" "$out"

# ---- libraries are not test projects --------------------------------------------------------------------------------------
make_repo "$tmp/libs" "$all" "$matrix_oneline"
run_check "$tmp/libs"
check "coverage: support libraries need no shard" "0" "$rc"

# ---- shard shape ---------------------------------------------------------------------------------------------------------
matrix_noproj='          - shard: api-1
            projects: Fake.Api.Tests Fake.Domain.Tests Fake.Infra.Tests
          - shard: orphan
            filter: >-
              FullyQualifiedName~X'
make_repo "$tmp/noproj" "$all" "$matrix_noproj"
run_check "$tmp/noproj"
check "coverage: a shard with no projects fails" "1" "$rc"
contains "coverage: ...naming the shard" "shard 'orphan' lists no projects" "$out"

make_repo "$tmp/none" "$all" "          - shard: x
            filter: foo"
run_check "$tmp/none"
check "coverage: a ci.yml with no projects anywhere fails" "1" "$rc"

# ---- the check cannot go blind ---------------------------------------------------------------------------------------------
make_repo "$tmp/blind" "$all" "$matrix_ok"
echo '<Project />' >"$tmp/blind/tests/Directory.Build.targets"
run_check "$tmp/blind"
check "coverage: a targets file that names no libraries is a setup error, not a pass (exit 2)" "2" "$rc"
contains "coverage: ...saying what to update" "update this check" "$out"

make_repo "$tmp/empty" "" "$matrix_ok"
run_check "$tmp/empty"
check "coverage: no test projects at all is a setup error (exit 2)" "2" "$rc"

rm -rf "$tmp/nowf/.github"; mkdir -p "$tmp/nowf/tests"; run_check "$tmp/nowf"
check "coverage: a missing targets file is a setup error (exit 2)" "2" "$rc"

# ---- this repository ---------------------------------------------------------------------------------------------------------
repo="$(cd "$here/../.." && pwd)"
run_check "$repo"
check "coverage: this repository's ci.yml covers every test project" "0" "$rc"
n_projects="$(ls -1d "$repo"/tests/*/*.csproj | wc -l | tr -d ' ')"
n_libs="$(grep -oE "MSBuildProjectName\)' *!= *'[A-Za-z0-9_.]+'" "$repo/tests/Directory.Build.targets" | sort -u | wc -l | tr -d ' ')"
contains "coverage: ...counting every project but the libraries" "all $((n_projects - n_libs)) test projects" "$out"
# The real thing, with one project taken out of ci.yml, must fail: copy the repo's inputs and drop a name.
copy="$tmp/real"
rm -rf "$copy"; mkdir -p "$copy/.github/workflows" "$copy/tests"
cp "$repo/tests/Directory.Build.targets" "$copy/tests/"
for d in "$repo"/tests/*/; do n="$(basename "$d")"; [[ -f "$d/$n.csproj" ]] && { mkdir -p "$copy/tests/$n"; : >"$copy/tests/$n/$n.csproj"; }; done
sed 's/CollectionMaxxing\.Domain\.Tests/CollectionMaxxing.Nothing/' "$repo/.github/workflows/ci.yml" >"$copy/.github/workflows/ci.yml"
run_check "$copy"
check "coverage: dropping a project from this repository's ci.yml is caught" "1" "$rc"
contains "coverage: ...naming the project that fell out" "tests/CollectionMaxxing.Domain.Tests is not run" "$out"

echo "SUMMARY passed=$pass failed=$fail"
((fail == 0))
