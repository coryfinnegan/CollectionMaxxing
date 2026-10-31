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

# ---- catching up on a missed night ---------------------------------------------------------------------------------
# From here on the clock is ours: cm_now answers FAKE_NOW, in UTC, so "yesterday at 03:00" means the same everywhere.
export TZ=UTC
export CM_RUN_MARKER="$tmp/running"
FAKE_NOW=0
cm_now() { echo "$FAKE_NOW"; }
at() { date -u -d "$1" +%s; }                                   # at "2026-10-13 10:00:00" -> epoch seconds (UTC)
stamp() { date -u -d "$1" +%Y%m%d-%H%M%S; }                    # a backup name's timestamp for that moment
mkbackup() { # dir "YYYY-MM-DD HH:MM:SS": a successful backup (dump and manifest, as a finished run leaves them)
  mkdir -p "$1/db"
  echo "fake dump" >"$1/db/collectionmaxxing-$(stamp "$2").dump"
  echo '{}' >"$1/db/collectionmaxxing-$(stamp "$2").manifest.json"
}
due() { cm_backup_due '0 3 * * *' "$1" && echo due || echo "up to date"; }   # $1 = backup dir; FAKE_NOW = the moment

cdir="$tmp/catchup"
rm -rf "$cdir"; mkdir -p "$cdir"
FAKE_NOW="$(at '2026-10-13 10:00:00')"
check "catch-up: no backup at all is due" "due" "$(due "$cdir")"

mkbackup "$cdir" "2026-10-12 03:00:05"
check "catch-up: last night's backup is missing, so a backup is due" "due" "$(due "$cdir")"

mkbackup "$cdir" "2026-10-13 03:00:05"
check "catch-up: tonight's run already happened is up to date" "up to date" "$(due "$cdir")"

FAKE_NOW="$(at '2026-10-13 23:59:59')"
check "catch-up: a backup 21 hours old, with the next run still ahead, is up to date" "up to date" "$(due "$cdir")"

cdir2="$tmp/catchup2"
rm -rf "$cdir2"; mkbackup "$cdir2" "2026-10-12 03:00:05"
FAKE_NOW="$(at '2026-10-13 02:00:00')"
check "catch-up: before tonight's run, last night's backup is up to date" "up to date" "$(due "$cdir2")"
FAKE_NOW="$(at '2026-10-13 03:00:30')"
check "catch-up: a deploy at 03:00:30 that killed the 03:00:00 run catches up, though the newest backup is only 24 h old" "due" "$(due "$cdir2")"
FAKE_NOW="$(at '2026-10-13 02:59:00')"
check "catch-up: one minute before the run the container just waits" "up to date" "$(due "$cdir2")"

cdir3="$tmp/catchup3"
rm -rf "$cdir3"; mkdir -p "$cdir3/db"
echo "fake dump" >"$cdir3/db/collectionmaxxing-$(stamp '2026-10-13 03:00:05').dump"   # a dump whose run never finished: no manifest
echo "fake dump" >"$cdir3/db/collectionmaxxing-$(stamp '2026-10-13 03:00:06').dump.partial"
FAKE_NOW="$(at '2026-10-13 10:00:00')"
check "catch-up: a dump with no manifest is not a successful backup" "due" "$(due "$cdir3")"
cdir4="$tmp/catchup4"
rm -rf "$cdir4"; mkdir -p "$cdir4/db"
echo '{}' >"$cdir4/db/collectionmaxxing-$(stamp '2026-10-13 03:00:05').manifest.json"   # a manifest with no dump
check "catch-up: a manifest with no dump is not a successful backup" "due" "$(due "$cdir4")"
check "catch-up: the newest successful backup is the newest one that has both a manifest and a dump" "2026-10-12 03:00:05" "$(mkbackup "$cdir3" '2026-10-12 03:00:05'; date -u -d "@$(cm_newest_backup_epoch "$cdir3")" '+%Y-%m-%d %H:%M:%S')"
check "catch-up: a schedule that is not daily is refused (never due)" "1" "$(cm_backup_due '*/5 * * * *' "$cdir" >/dev/null; echo $?)"

# cm_catch_up runs the real backup code (against the fakes above): a stale newest backup -> a new dump; a fresh one -> none.
catch_dir="$tmp/catch-run"
cdump_count() { ls -1 "$catch_dir/db" 2>/dev/null | grep -c '\.dump$' || true; }
rm -rf "$catch_dir"; mkdir -p "$catch_dir/src"; echo cover >"$catch_dir/src/a.jpg"
FAKE_NOW="$(date +%s)"
mkbackup "$catch_dir" "$(date -u -d '3 days ago' '+%Y-%m-%d %H:%M:%S')"
: >"$curl_calls"
BACKUP_DIR="$catch_dir" FILES_DIR="$catch_dir/src" CM_HEARTBEAT_URL="https://hc.example/abc" cm_catch_up '0 3 * * *' >"$tmp/catch.out" 2>&1
check "catch-up: a stale newest backup is made up for at start" "2" "$(cdump_count)"
check "catch-up: ...and the new one is a whole backup (manifest too)" "2" "$(ls -1 "$catch_dir/db" | grep -c '\.manifest\.json$')"
check "catch-up: ...and says why" "true" "$(grep -q 'backing up now to catch up' "$tmp/catch.out" && echo true || echo false)"
check "catch-up: ...and pings the heartbeat" "-fsS -m 10 --retry 3 -o /dev/null https://hc.example/abc" "$(cat "$curl_calls")"
rm -rf "$catch_dir"; mkdir -p "$catch_dir/src"; echo cover >"$catch_dir/src/a.jpg"
mkbackup "$catch_dir" "$(date -u -d '10 minutes ago' '+%Y-%m-%d %H:%M:%S')"
: >"$curl_calls"
BACKUP_DIR="$catch_dir" FILES_DIR="$catch_dir/src" CM_HEARTBEAT_URL="https://hc.example/abc" cm_catch_up '0 3 * * *' >"$tmp/catch.out" 2>&1
check "catch-up: a fresh newest backup means no backup at start" "1" "$(cdump_count)"
check "catch-up: ...and no heartbeat" "0" "$(wc -l <"$curl_calls" | tr -d ' ')"
rm -rf "$catch_dir"; mkdir -p "$catch_dir/src"
FAKE_DUMP_FAIL=1 BACKUP_DIR="$catch_dir" FILES_DIR="$catch_dir/src" cm_catch_up '0 3 * * *' >"$tmp/catch.out" 2>&1; rc=$?
check "catch-up: a failed catch-up does not stop the daemon (returns 0)" "0" "$rc"
check "catch-up: ...it logs the failure" "true" "$(grep -q 'catch-up backup failed' "$tmp/catch.out" && echo true || echo false)"
FAKE_DUMP_FAIL=0

# ---- the health check ----------------------------------------------------------------------------------------------
FAKE_PG_READY=1
pg_isready() { ((FAKE_PG_READY == 1)); }
hdir="$tmp/health"
health() { out="$(BACKUP_DIR="$hdir" cm_health_check)"; rc=$?; }   # sets out and rc

rm -rf "$hdir"; mkbackup "$hdir" "2026-10-13 03:00:05"
FAKE_NOW="$(at '2026-10-13 05:00:00')"; health
check "health: a backup from this morning is healthy" "0" "$rc"
FAKE_NOW="$(at '2026-10-14 09:00:05')"; health   # exactly 30 h
check "health: exactly 30 hours old is still healthy" "0" "$rc"
FAKE_NOW="$(at '2026-10-14 09:00:06')"; health   # 30 h and a second
check "health: over 30 hours old is unhealthy" "1" "$rc"
check "health: ...and says how old" "true" "$([[ "$out" == *"unhealthy"*"30 h old"* ]] && echo true || echo "false($out)")"
FAKE_NOW="$(at '2026-10-16 03:00:00')"; health
check "health: three missed nights are unhealthy" "1" "$rc"

FAKE_NOW="$(at '2026-10-13 05:00:00')"; FAKE_PG_READY=0; health; FAKE_PG_READY=1
check "health: Postgres unreachable is unhealthy, whatever the age" "1" "$rc"
check "health: ...and says so" "true" "$([[ "$out" == *"Postgres is not reachable"* ]] && echo true || echo "false($out)")"

rm -rf "$hdir"; mkdir -p "$hdir/db"; health
check "health: no backup at all is unhealthy" "1" "$rc"
echo "fake dump" >"$hdir/db/collectionmaxxing-$(stamp '2026-10-13 03:00:05').dump"; health
check "health: a dump without a manifest does not make it healthy" "1" "$rc"

# A backup that is running right now keeps a stale container healthy (the catch-up after a long gap); a hung one does not.
rm -rf "$hdir"; mkbackup "$hdir" "2026-10-10 03:00:05"
FAKE_NOW="$(at '2026-10-13 10:00:00')"
health;                                                         check "health: stale and idle is unhealthy" "1" "$rc"
echo "$((FAKE_NOW - 60))" >"$CM_RUN_MARKER"; health;             check "health: stale but a backup started a minute ago is healthy" "0" "$rc"
check "health: ...and says a backup is running" "healthy: a backup is running" "$out"
echo "$((FAKE_NOW - 7201))" >"$CM_RUN_MARKER"; health;           check "health: stale with a run marker over 2 hours old (a hung run) is unhealthy" "1" "$rc"
echo "garbage" >"$CM_RUN_MARKER"; health;                        check "health: an unreadable run marker counts for nothing" "1" "$rc"
rm -f "$CM_RUN_MARKER"
FAKE_PG_READY=0; echo "$((FAKE_NOW - 60))" >"$CM_RUN_MARKER"; health; FAKE_PG_READY=1; rm -f "$CM_RUN_MARKER"
check "health: a running backup does not excuse an unreachable Postgres" "1" "$rc"

# The marker exists exactly while a run runs, and is gone after a success and after a failure.
marker_during="$tmp/marker-during"
pg_dump() { [[ -f "$CM_RUN_MARKER" ]] && echo yes >"$marker_during"; [[ "$FAKE_DUMP_FAIL" == 1 ]] && return 1; local out=""; while (($# > 0)); do [[ "$1" == "-f" ]] && out="$2"; shift; done; echo "fake dump" >"$out"; }
setup_run "$run_dir"; rm -f "$marker_during"
BACKUP_DIR="$run_dir" FILES_DIR="$run_dir/src" cm_backup_run >/dev/null 2>&1
check "health: the run marker exists while the dump is being taken" "yes" "$(cat "$marker_during" 2>/dev/null)"
check "health: ...and is gone after a successful run" "false" "$([[ -e "$CM_RUN_MARKER" ]] && echo true || echo false)"
setup_run "$run_dir"
FAKE_DUMP_FAIL=1 BACKUP_DIR="$run_dir" FILES_DIR="$run_dir/src" cm_backup_run >/dev/null 2>&1; FAKE_DUMP_FAIL=0
check "health: ...and gone after a failed run" "false" "$([[ -e "$CM_RUN_MARKER" ]] && echo true || echo false)"

# ---- the daemon and the health mode, as the container runs them ----------------------------------------------------
# backup.sh in a child bash, with fake programs on the PATH. The fake sleep ends the daemon (it kills its parent) the
# first time the daemon goes to wait for the next night, so the test never waits and nothing is left running.
bin="$tmp/bin"
mkdir -p "$bin"
cat >"$bin/pg_dump" <<'FAKE'
#!/usr/bin/env bash
out=""; while (($# > 0)); do [[ "$1" == "-f" ]] && out="$2"; shift; done; echo "fake dump" >"$out"
FAKE
cat >"$bin/pg_restore" <<'FAKE'
#!/usr/bin/env bash
exit 0
FAKE
cat >"$bin/curl" <<'FAKE'
#!/usr/bin/env bash
exit 0
FAKE
cat >"$bin/psql" <<'FAKE'
#!/usr/bin/env bash
sql="$*"
if [[ "$sql" == *information_schema* ]]; then printf 'items|3\nusers|1\n'
elif [[ "$sql" == *migration_id* ]]; then echo 20261013153821_InitialCreate
else exit 3; fi
FAKE
cat >"$bin/pg_isready" <<'FAKE'
#!/usr/bin/env bash
[[ "${FAKE_PG_READY:-1}" == 1 ]]
FAKE
cat >"$bin/sleep" <<'FAKE'
#!/usr/bin/env bash
echo "sleep $*" >>"$FAKE_SLEEP_LOG"
kill "$PPID"
FAKE
chmod +x "$bin"/*

ddir="$tmp/daemon"
daemon() { # runs backup.sh daemon against $ddir; output in $out
  : >"$tmp/sleep.log"
  out="$(PATH="$bin:$PATH" FAKE_SLEEP_LOG="$tmp/sleep.log" BACKUP_DIR="$ddir" FILES_DIR="$ddir/src" CM_RUN_MARKER="$tmp/daemon-running" \
    timeout 60 bash "$here/../backup/backup.sh" daemon 2>&1)"
}
ddumps() { ls -1 "$ddir/db" 2>/dev/null | grep -c '\.dump$' || true; }

rm -rf "$ddir"; mkdir -p "$ddir/src"; echo cover >"$ddir/src/a.jpg"
mkbackup "$ddir" "$(date -u -d '3 days ago' '+%Y-%m-%d %H:%M:%S')"
daemon
check "daemon: a stale newest backup is backed up at start" "2" "$(ddumps)"
check "daemon: ...before it goes to wait for the next night" "true" "$([[ "$out" == *"catch up"*"next backup in"* ]] && echo true || echo "false($out)")"
check "daemon: ...and then waits once for the schedule (one sleep, not a run per loop)" "1" "$(grep -c '^sleep ' "$tmp/sleep.log")"

rm -rf "$ddir"; mkdir -p "$ddir/src"; echo cover >"$ddir/src/a.jpg"
mkbackup "$ddir" "$(date -u -d '10 minutes ago' '+%Y-%m-%d %H:%M:%S')"
daemon
check "daemon: a fresh newest backup means no backup at start" "1" "$(ddumps)"
check "daemon: ...it just waits for the next night" "true" "$([[ "$out" == *"up to date"*"next backup in"* ]] && echo true || echo "false($out)")"

rm -rf "$ddir"; mkdir -p "$ddir/src"; echo cover >"$ddir/src/a.jpg"
daemon
check "daemon: a first start with no backup at all backs up at once" "1" "$(ddumps)"

health_mode() { PATH="$bin:$PATH" BACKUP_DIR="$ddir" CM_RUN_MARKER="$tmp/daemon-running" bash "$here/../backup/backup.sh" health; }
rm -rf "$ddir"; mkbackup "$ddir" "$(date -u -d '2 hours ago' '+%Y-%m-%d %H:%M:%S')"
out="$(health_mode)"; rc=$?
check "health mode: a recent backup exits 0" "0" "$rc"
rm -rf "$ddir"; mkbackup "$ddir" "$(date -u -d '31 hours ago' '+%Y-%m-%d %H:%M:%S')"
out="$(health_mode)"; rc=$?
check "health mode: a backup 31 hours old exits 1" "1" "$rc"
check "health mode: ...and prints the reason" "true" "$([[ "$out" == *unhealthy*"31 h old"* ]] && echo true || echo "false($out)")"
rm -rf "$ddir"; mkbackup "$ddir" "$(date -u -d '2 hours ago' '+%Y-%m-%d %H:%M:%S')"
out="$(FAKE_PG_READY=0 health_mode)"; rc=$?
check "health mode: Postgres unreachable exits 1" "1" "$rc"
check "health mode: an unknown mode is refused" "2" "$(PATH="$bin:$PATH" bash "$here/../backup/backup.sh" bogus >/dev/null 2>&1; echo $?)"

echo "SUMMARY passed=$pass failed=$fail"
((fail == 0))
