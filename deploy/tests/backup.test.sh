#!/usr/bin/env bash
# Tests for deploy/backup/backup-lib.sh. Prints "PASS name" or "FAIL name: why" per test, then a summary line.
# Uses fake pg_dump / pg_restore / curl (bash functions beat real programs); touches only a temp folder.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=../backup/backup-lib.sh
. "$here/../backup/backup-lib.sh"

pass=0
fail=0
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

ok() { pass=$((pass + 1)); echo "PASS $1"; }
bad() { fail=$((fail + 1)); echo "FAIL $1: $2"; }
check() { # name expected actual
  if [[ "$2" == "$3" ]]; then ok "$1"; else bad "$1" "expected [$2] but got [$3]"; fi
}

# ---- fakes ------------------------------------------------------------------------------------------------------
FAKE_DUMP_FAIL=0
curl_calls="$tmp/curl.calls"
pg_dump() {
  [[ "$FAKE_DUMP_FAIL" == 1 ]] && return 1
  local out="" ; while (($# > 0)); do [[ "$1" == "-f" ]] && out="$2"; shift; done
  echo "fake dump" >"$out"
}
pg_restore() { return 0; }
curl() { echo "$*" >>"$curl_calls"; return 0; }
# psql answers the three questions the manifest asks: table counts, newest migration id, and nothing else.
# It never returns row data (a real psql could; the manifest code must only ever ask for counts).
FAKE_PSQL_FAIL=0
FAKE_COUNTS=$'items|3\nusers|1\n__EFMigrationsHistory|1'
psql() {
  [[ "$FAKE_PSQL_FAIL" == 1 ]] && return 2
  local sql="$*"
  if [[ "$sql" == *information_schema* ]]; then printf '%s\n' "$FAKE_COUNTS"
  elif [[ "$sql" == *migration_id* ]]; then echo "20261013153821_InitialCreate"
  else echo "unexpected psql call: $sql" >&2; return 3
  fi
}

# ---- 30 days of daily dumps --------------------------------------------------------------------------------------
names=""
for i in $(seq 0 29); do
  d="$(date -d "2026-10-13 - $i days" +%Y%m%d)"
  names+="collectionmaxxing-${d}-030000.dump"$'\n'
done
names="${names%$'\n'}"

kept="$(printf '%s\n' "$names" | cm_retention_keep | LC_ALL=C sort)"
expected_keep="$(printf '%s\n' \
  20260920 20260927 \
  20260930 20261001 20261002 20261003 20261004 20261005 20261006 20261007 20261008 20261009 20261010 20261011 20261012 20261013 \
  | sed 's/^\(.*\)$/collectionmaxxing-\1-030000.dump/' | LC_ALL=C sort)"
check "retention: 30 days keeps 14 daily plus the last day of each older week" "$expected_keep" "$kept"

deleted="$(printf '%s\n' "$names" | cm_retention_delete | wc -l | tr -d ' ')"
check "retention: the other 14 of 30 are deleted" "14" "$deleted"

newest_survives="$(printf '%s\n' "$names" | CM_KEEP_DAILY=0 CM_KEEP_WEEKLY=0 CM_KEEP_MONTHLY=0 cm_retention_delete | grep -c 20261013 || true)"
check "retention: the newest backup is never deleted, whatever the policy" "0" "$newest_survives"

# Long history: 400 days. Monthly rule keeps the newest of each of the last 12 months; weekly the last 8 weeks.
long=""
for i in $(seq 0 399); do long+="collectionmaxxing-$(date -d "2026-10-13 - $i days" +%Y%m%d)-030000.dump"$'\n'; done
long_kept="$(printf '%s' "$long" | cm_retention_keep | wc -l | tr -d ' ')"
check "retention: 400 days stay bounded (14 daily + 8 weekly + 12 monthly, overlaps merged)" "true" "$([[ "$long_kept" -le 34 && "$long_kept" -ge 20 ]] && echo true || echo "false($long_kept)")"

# Names that are not ours are never listed for deletion.
foreign="$(printf 'notes.txt\ncollectionmaxxing-20260101-030000.dump.partial\nsomething.dump\n' | cm_retention_delete | wc -l | tr -d ' ')"
check "retention: files without a timestamp are ignored" "0" "$foreign"

# ---- cm_prune only touches exact matches -------------------------------------------------------------------------
pdir="$tmp/prune"
mkdir -p "$pdir"
for i in $(seq 0 29); do : >"$pdir/collectionmaxxing-$(date -d "2026-10-13 - $i days" +%Y%m%d)-030000.dump"; done
: >"$pdir/readme.txt"; : >"$pdir/collectionmaxxing-20260101-030000.dump.partial"
cm_prune "$pdir" collectionmaxxing dump >/dev/null
check "prune: removes only the 14 old dumps" "16" "$(ls -1 "$pdir" | grep -c '^collectionmaxxing-.*\.dump$')"
check "prune: leaves foreign files alone" "2" "$(ls -1 "$pdir" | grep -cE '^(readme\.txt|.*\.partial)$')"

# ---- heartbeat ---------------------------------------------------------------------------------------------------
: >"$curl_calls"
CM_HEARTBEAT_URL="https://hc.example/abc" cm_heartbeat ok
check "heartbeat: success pings the URL" "-fsS -m 10 --retry 3 -o /dev/null https://hc.example/abc" "$(cat "$curl_calls")"
: >"$curl_calls"
CM_HEARTBEAT_URL="https://hc.example/abc/" cm_heartbeat fail
check "heartbeat: failure pings /fail" "-fsS -m 10 --retry 3 -o /dev/null https://hc.example/abc/fail" "$(cat "$curl_calls")"
: >"$curl_calls"
CM_HEARTBEAT_URL="" cm_heartbeat ok; CM_HEARTBEAT_URL="" cm_heartbeat fail
check "heartbeat: an empty URL pings nothing" "0" "$(wc -l <"$curl_calls" | tr -d ' ')"

# ---- a whole run -------------------------------------------------------------------------------------------------
setup_run() { # dir
  rm -rf "$1"; mkdir -p "$1/db" "$1/files" "$1/src"; echo cover >"$1/src/a.jpg"
  for i in $(seq 0 29); do
    d="$(date -d "2020-03-31 - $i days" +%Y%m%d)"
    : >"$1/db/collectionmaxxing-${d}-030000.dump"; : >"$1/files/files-${d}-030000.tar.gz"
  done
}
run_dir="$tmp/run"

setup_run "$run_dir"; : >"$curl_calls"
FAKE_DUMP_FAIL=1 BACKUP_DIR="$run_dir" FILES_DIR="$run_dir/src" CM_HEARTBEAT_URL="https://hc.example/abc" cm_backup_run >/dev/null; rc=$?
check "run: a failed dump returns failure" "1" "$rc"
check "run: a failed dump never prunes" "30" "$(ls -1 "$run_dir/db" | wc -l | tr -d ' ')"
check "run: a failed dump pings /fail" "-fsS -m 10 --retry 3 -o /dev/null https://hc.example/abc/fail" "$(cat "$curl_calls")"
check "run: a failed dump leaves no partial file" "0" "$(ls -1 "$run_dir/db" | grep -c partial || true)"

setup_run "$run_dir"; : >"$curl_calls"
FAKE_DUMP_FAIL=0 BACKUP_DIR="$run_dir" FILES_DIR="$run_dir/src" CM_HEARTBEAT_URL="https://hc.example/abc" cm_backup_run >/dev/null; rc=$?
check "run: success returns 0" "0" "$rc"
check "run: success writes a new dump" "1" "$(ls -1 "$run_dir/db" | grep -c "^collectionmaxxing-$(date -u +%Y).*\.dump$")"
check "run: success writes a files archive that holds the files" "./a.jpg" "$(tar -tzf "$(ls -1 "$run_dir"/files/files-$(date -u +%Y)*.tar.gz | head -n1)" | grep a.jpg)"
check "run: success prunes old dumps" "true" "$([[ "$(ls -1 "$run_dir/db" | wc -l | tr -d ' ')" -lt 31 ]] && echo true || echo false)"
check "run: success pings the URL" "-fsS -m 10 --retry 3 -o /dev/null https://hc.example/abc" "$(cat "$curl_calls")"

# ---- manifest ------------------------------------------------------------------------------------------------------
mdir="$tmp/manifest"
rm -rf "$mdir"; mkdir -p "$mdir/src/covers"
echo "cover one SENTINEL-ROW-DATA" >"$mdir/src/covers/one.jpg"; echo "cover two" >"$mdir/src/covers/two.jpg"
: >"$curl_calls"
BACKUP_DIR="$mdir" FILES_DIR="$mdir/src" CM_HEARTBEAT_URL="https://hc.example/abc" cm_backup_run >/dev/null; rc=$?
mf="$(ls -1 "$mdir"/db/collectionmaxxing-*.manifest.json 2>/dev/null | head -n1)"
dump="$(ls -1 "$mdir"/db/collectionmaxxing-*.dump | head -n1)"
check "manifest: a run writes one next to the dump with the same timestamp" "$(basename "${dump%.dump}").manifest.json" "$(basename "$mf")"
check "manifest: the run still succeeds" "0" "$rc"
check "manifest: records the row count of each table" "true" "$(grep -q '"items": 3' "$mf" && grep -q '"users": 1' "$mf" && echo true || echo false)"
check "manifest: records the newest migration as the schema version" '"schemaVersion": "20261013153821_InitialCreate",' "$(grep schemaVersion "$mf" | sed 's/^ *//')"
check "manifest: records the dump's sha256" "$(sha256sum "$dump" | cut -d' ' -f1)" "$(sed -n 's/.*"dumpSha256": "\([0-9a-f]*\)".*/\1/p' "$mf")"
check "manifest: lists every file with its sha256" "true" "$(grep -q "\"path\": \"covers/one.jpg\", \"sha256\": \"$(sha256sum "$mdir/src/covers/one.jpg" | cut -d' ' -f1)\"" "$mf" && grep -q '"path": "covers/two.jpg"' "$mf" && echo true || echo false)"
check "manifest: records the files archive and its sha256" "$(sha256sum "$(ls -1 "$mdir"/files/files-*.tar.gz | head -n1)" | cut -d' ' -f1)" "$(sed -n 's/.*"archiveSha256": "\([0-9a-f]*\)".*/\1/p' "$mf")"
check "manifest: holds counts and hashes only, no row or file content" "0" "$(grep -c 'SENTINEL-ROW-DATA' "$mf" || true)"
# Every line must be one of the known shapes: a number, a hash, a file name, a time or a table name. Nothing else can get in.
odd="$(grep -vE '^\s*[]{}],?$|^\s*"(manifestVersion|dumpBytes|fileCount)": [0-9]+,?$|^\s*"(createdAt|timestamp|schemaVersion|dumpFile|archiveFile)": "[A-Za-z0-9_.:-]+",?$|^\s*"(dumpSha256|archiveSha256)": "[0-9a-f]{64}",?$|^\s*"(tableCountsBefore|tableCountsAfter|files)": [{[]$|^\s*"[A-Za-z0-9_]+": [0-9]+,?$|^\s*\{"path": "[A-Za-z0-9_./ -]+", "sha256": "[0-9a-f]{64}"\},?$' "$mf" || true)"
check "manifest: every line is a count, a hash, a name or a time" "" "$odd"
check "manifest: the file hash list is the files archive, not the live folder" "true" "$(grep -q '"fileCount": 2' "$mf" && echo true || echo false)"
check "manifest: leaves no partial or temp file behind" "0" "$(find "$mdir" -name '*.partial' -o -name '.manifest-tmp*' | wc -l | tr -d ' ')"

# A run that cannot read the database for the manifest fails (and pings /fail), and never prunes.
setup_run "$run_dir"; : >"$curl_calls"
FAKE_PSQL_FAIL=1 BACKUP_DIR="$run_dir" FILES_DIR="$run_dir/src" CM_HEARTBEAT_URL="https://hc.example/abc" cm_backup_run >/dev/null 2>&1; rc=$?
FAKE_PSQL_FAIL=0
check "manifest: no manifest means the run fails" "1" "$rc"
check "manifest: a failed run pings /fail" "-fsS -m 10 --retry 3 -o /dev/null https://hc.example/abc/fail" "$(cat "$curl_calls")"
check "manifest: a failed run never prunes" "30" "$(ls -1 "$run_dir/db" | grep -c '\.dump$')"
check "manifest: a failed run leaves no manifest" "0" "$(ls -1 "$run_dir/db" | grep -c manifest || true)"

# A failed dump writes no manifest.
setup_run "$run_dir"
FAKE_DUMP_FAIL=1 BACKUP_DIR="$run_dir" FILES_DIR="$run_dir/src" cm_backup_run >/dev/null 2>&1
FAKE_DUMP_FAIL=0
check "manifest: a failed dump writes no manifest" "0" "$(ls -1 "$run_dir/db" | grep -c manifest || true)"

# Pruning a dump prunes its manifest; a kept dump keeps its manifest; foreign files stay.
pm="$tmp/prune-manifest"
rm -rf "$pm"; mkdir -p "$pm"
for i in $(seq 0 29); do
  d="$(date -d "2026-10-13 - $i days" +%Y%m%d)"
  : >"$pm/collectionmaxxing-${d}-030000.dump"; echo '{}' >"$pm/collectionmaxxing-${d}-030000.manifest.json"
done
: >"$pm/collectionmaxxing-20260101-030000.manifest.json.keep"; : >"$pm/notes.manifest.json"
cm_prune "$pm" collectionmaxxing dump >/dev/null
check "manifest: pruned dumps take their manifests with them" "16" "$(ls -1 "$pm" | grep -c '^collectionmaxxing-.*\.manifest\.json$')"
check "manifest: every kept dump still has its manifest" "0" "$(for f in "$pm"/*.dump; do [[ -f "${f%.dump}.manifest.json" ]] || echo missing; done | wc -l | tr -d ' ')"
check "manifest: foreign files are never touched" "2" "$(ls -1 "$pm" | grep -cE '^(notes\.manifest\.json|.*\.keep)$')"

# ---- schedule parsing ----------------------------------------------------------------------------------------------
check "schedule: a daily 'M H * * *' is accepted" "0" "$(cm_seconds_until '0 3 * * *' >/dev/null; echo $?)"
check "schedule: anything else is refused" "1" "$(cm_seconds_until '*/5 * * * *' >/dev/null; echo $?)"
check "schedule: an hour past 23 is refused" "1" "$(cm_seconds_until '0 25 * * *' >/dev/null; echo $?)"

echo "SUMMARY passed=$pass failed=$fail"
((fail == 0))
