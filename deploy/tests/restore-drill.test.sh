#!/usr/bin/env bash
# Tests for scripts/restore-drill.sh against REAL throwaway Postgres containers. Prints "PASS name" or "FAIL name: why".
#
# What this starts (all removed at the end, and only these):
#   cm-ops1-test-src-*    a Postgres seeded with known rows, the stand-in for the live database (no network, no volumes)
#   cm-ops1-test-drill-*  the restore drill's own throwaway containers (the script names them from CM_DRILL_PREFIX)
# Nothing here touches the real stack, a real volume, a real backup folder or any port: the containers run with
# --network none. Needs a running Docker daemon and the postgres:17 image (it is pulled if missing).
set -u
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' # Git for Windows: do not rewrite /tmp-style paths inside docker arguments
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=../backup/backup-lib.sh
. "$here/../backup/backup-lib.sh"
drill="$here/../../scripts/restore-drill.sh"

pass=0
fail=0
ok() { pass=$((pass + 1)); echo "PASS $1"; }
bad() { fail=$((fail + 1)); echo "FAIL $1: $2"; }
check() { if [[ "$2" == "$3" ]]; then ok "$1"; else bad "$1" "expected [$2] but got [$3]"; fi; }
contains() { if [[ "$3" == *"$2"* ]]; then ok "$1"; else bad "$1" "expected to find [$2] in: $(printf '%s' "$3" | tail -n 12)"; fi; }

IMAGE="${CM_DRILL_IMAGE:-postgres:17}"
if ! docker info >/dev/null 2>&1; then
  bad "restore drill tests need Docker" "the Docker daemon is not reachable (start Docker, or pass -SkipDocker to Run-Tests.ps1)"
  echo "SUMMARY passed=$pass failed=$fail"
  exit 1
fi

tmp="$(mktemp -d)"
src="cm-ops1-test-src-$$-$RANDOM"
cleanup() {
  # Only containers this script created, by exact name or by our own test prefix.
  docker rm -fv "$src" >/dev/null 2>&1
  for c in $(docker ps -a --filter "name=cm-ops1-test-drill-$$" --format '{{.Names}}' 2>/dev/null); do
    case "$c" in cm-ops1-test-drill-"$$"-*) docker rm -fv "$c" >/dev/null 2>&1 ;; esac
  done
  rm -rf "$tmp"
}
trap cleanup EXIT

dsql() { docker exec -i "$src" psql -U collectionmaxxing -d collectionmaxxing -X -v ON_ERROR_STOP=1 "$@"; }

# ---- the stand-in live database, seeded with known rows -----------------------------------------------------------------
docker run -d --name "$src" --network none -e POSTGRES_USER=collectionmaxxing -e POSTGRES_DB=collectionmaxxing \
  -e POSTGRES_PASSWORD=test-only-not-a-secret "$IMAGE" >/dev/null || { bad "setup" "could not start $IMAGE"; echo "SUMMARY passed=$pass failed=$fail"; exit 1; }
ready=0
for _ in $(seq 1 60); do
  docker exec "$src" pg_isready -h 127.0.0.1 -U collectionmaxxing >/dev/null 2>&1 && { ready=1; break; }
  sleep 1
done
((ready == 1)) || { bad "setup" "the source database never became ready"; echo "SUMMARY passed=$pass failed=$fail"; exit 1; }

dsql >/dev/null <<'SQL'
create table items (id int primary key, title text not null);
create table users (id int primary key, name text not null);
-- Same shape as production: EFCore.NamingConventions snake_cases the history columns too.
create table "__EFMigrationsHistory" (migration_id varchar(150) primary key, product_version varchar(32) not null);
insert into items values (1, 'first'), (2, 'second'), (3, 'third');
insert into users values (1, 'owner');
insert into "__EFMigrationsHistory" values ('20261013153821_InitialCreate', '10.0.0'), ('20261014090000_AddThings', '10.0.0');
SQL

# The backup job's own code runs against the real database through docker exec (so the manifest is the real thing).
pg_dump() { local out=""; while (($# > 0)); do [[ "$1" == "-f" ]] && out="$2"; shift; done; docker exec -i "$src" pg_dump -U collectionmaxxing -Fc collectionmaxxing >"$out"; }
pg_restore() { local f="${*: -1}"; docker exec -i "$src" pg_restore --list <"$f" >/dev/null; }
psql() { docker exec -i "$src" psql -U collectionmaxxing -d collectionmaxxing "$@"; }
curl() { return 0; }

files="$tmp/files"; mkdir -p "$files/covers"
echo "cover one" >"$files/covers/one.jpg"; echo "cover two" >"$files/covers/two.jpg"
good="$tmp/backup"
BACKUP_DIR="$good" FILES_DIR="$files" cm_backup_run >/dev/null 2>&1
check "setup: the real backup job wrote a dump and a manifest" "1 1" "$(ls "$good"/db/*.dump | wc -l | tr -d ' ') $(ls "$good"/db/*.manifest.json | wc -l | tr -d ' ')"
dump_name="$(basename "$(ls "$good"/db/*.dump)")"

export CM_DRILL_PREFIX="cm-ops1-test-drill-$$"
export CM_DRILL_IMAGE="$IMAGE"
run_drill() { out="$(bash "$drill" "$@" 2>&1)"; rc=$?; }

# ---- PASS: counts match the manifest -------------------------------------------------------------------------------------
run_drill "$good"
check "drill: a good backup passes (exit 0)" "0" "$rc"
contains "drill: prints PASS with the numbers (items)" "PASS table items: restored 3, expected 3" "$out"
contains "drill: prints the numbers for every table" "PASS table users: restored 1, expected 1" "$out"
contains "drill: checks the migrations" "PASS migrations: newest applied is 20261014090000_AddThings" "$out"
contains "drill: checks the dump against the manifest hash" "PASS dump sha256 matches the manifest" "$out"
contains "drill: checks the files by hash list" "PASS files: 2 restored, 2 expected, every hash matches" "$out"
contains "drill: ends with an overall PASS" "RESULT: PASS" "$out"
contains "drill: uses the newest backup in the folder" "$dump_name" "$out"
check "drill: leaves no container behind" "" "$(docker ps -a --filter "name=$CM_DRILL_PREFIX" --format '{{.Names}}')"

run_drill "$good/db/$dump_name"
check "drill: a path to one dump works too" "0" "$rc"

# ---- the newest backup wins ----------------------------------------------------------------------------------------------
two="$tmp/two"; cp -r "$good" "$two"
old="collectionmaxxing-20200101-030000"
cp "$good/db/$dump_name" "$two/db/$old.dump"
run_drill "$two"
contains "drill: picks the newest of two backups" "$dump_name" "$out"
check "drill: ...and not the older one" "0" "$(printf '%s' "$out" | grep -c "$old" || true)"

# ---- isolation: only its own container, no network, no ports, no volumes ---------------------------------------------------
spy="$tmp/docker-spy.sh"
printf '#!/usr/bin/env bash\necho "$*" >>"%s/docker.calls"\nexec docker "$@"\n' "$tmp" >"$spy"; chmod +x "$spy"
CM_DRILL_DOCKER="$spy" run_drill "$good"
check "drill isolation: the run itself passes" "0" "$rc"
calls="$tmp/docker.calls"
runs="$(grep -E '^run ' "$calls")"
check "drill isolation: exactly one container is created" "1" "$(printf '%s\n' "$runs" | grep -c .)"
contains "drill isolation: it has no network at all" "--network none" "$runs"
check "drill isolation: no published port, volume or mount" "0" "$(printf '%s\n' "$runs" | grep -cE '(^| )(-p|--publish|-v|--volume|--mount|--volumes-from|--network host|--privileged)( |$)' || true)"
check "drill isolation: it is named from the test prefix" "1" "$(printf '%s\n' "$runs" | grep -c -- "--name $CM_DRILL_PREFIX-")"
check "drill isolation: every command that runs inside a container targets only its own" "0" "$(grep -E '^(exec|cp|rm|stop|kill|inspect|logs) ' "$calls" | grep -vc "$CM_DRILL_PREFIX-" || true)"
check "drill isolation: it never lists, prunes or touches anything else" "0" "$(grep -cE '^(system|volume|compose|network|image|ps|container) ' "$calls" || true)"

# ---- FAIL: a row is missing from the dump --------------------------------------------------------------------------------
missing="$tmp/missing"; cp -r "$good" "$missing"
dsql -c "delete from items where id = 2" >/dev/null
BACKUP_DIR="$tmp/scratch" FILES_DIR="$files" cm_backup_run >/dev/null 2>&1   # a real dump of the database with one row less
new_dump="$(ls "$tmp"/scratch/db/*.dump)"
cp "$new_dump" "$missing/db/$dump_name"
# Keep the manifest honest about the dump's own hash so only the row counts disagree (as if the row vanished before the dump).
new_sha="$(sha256sum <"$new_dump" | cut -d' ' -f1)"; new_bytes="$(wc -c <"$new_dump" | tr -d ' ')"
sed -i -e "s/\"dumpSha256\": \"[0-9a-f]*\"/\"dumpSha256\": \"$new_sha\"/" -e "s/\"dumpBytes\": [0-9]*/\"dumpBytes\": $new_bytes/" "$missing/db/${dump_name%.dump}.manifest.json"
dsql -c "insert into items values (2, 'second')" >/dev/null
run_drill "$missing"
check "drill: a row missing from the dump fails (exit 1)" "1" "$rc"
contains "drill: ...and says which table, with the numbers" "FAIL table items: restored 2, expected 3" "$out"
contains "drill: ...the other tables still pass" "PASS table users: restored 1, expected 1" "$out"
contains "drill: ...overall FAIL" "RESULT: FAIL" "$out"
check "drill: a failed run leaves no container behind" "" "$(docker ps -a --filter "name=$CM_DRILL_PREFIX" --format '{{.Names}}')"

# ---- FAIL: corrupt or tampered inputs -------------------------------------------------------------------------------------
corrupt="$tmp/corrupt"; cp -r "$good" "$corrupt"
printf 'x' >>"$corrupt/db/$dump_name"
run_drill "$corrupt"
check "drill: a dump that does not match its manifest hash fails" "1" "$rc"
contains "drill: ...and says so" "FAIL dump sha256" "$out"

badfiles="$tmp/badfiles"; cp -r "$good" "$badfiles"
sed -i '0,/"sha256": "[0-9a-f]/s//"sha256": "z/' "$badfiles/db/${dump_name%.dump}.manifest.json"
run_drill "$badfiles"
check "drill: a file whose hash differs from the manifest fails" "1" "$rc"
contains "drill: ...and reports it" "FAIL files:" "$out"

nofile="$tmp/nofile"; cp -r "$good" "$nofile"; rm -f "$nofile"/files/*.tar.gz
run_drill "$nofile"
check "drill: a missing files archive fails" "1" "$rc"

nomf="$tmp/nomanifest"; cp -r "$good" "$nomf"; rm -f "$nomf"/db/*.manifest.json
run_drill "$nomf"
check "drill: a backup with no manifest fails (nothing to compare with)" "1" "$rc"
contains "drill: ...and says so" "FAIL no manifest" "$out"

empty="$tmp/empty"; mkdir -p "$empty/db"
run_drill "$empty"
check "drill: a folder with no backup is a setup error (exit 2)" "2" "$rc"

oldschema="$tmp/oldschema"; cp -r "$good" "$oldschema"
sed -i 's/"schemaVersion": "[A-Za-z0-9_]*"/"schemaVersion": "20990101000000_FromTheFuture"/' "$oldschema/db/${dump_name%.dump}.manifest.json"
run_drill "$oldschema"
check "drill: a schema version that differs from the manifest fails" "1" "$rc"
contains "drill: ...in the migrations check" "FAIL migrations" "$out"

echo "SUMMARY passed=$pass failed=$fail"
((fail == 0))
