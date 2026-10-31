#!/usr/bin/env bash
# The restore drill: proves a backup restores. Read deploy/RESTORE.md first.
#
#   scripts/restore-drill.sh [--confirm-live] [BACKUP_FOLDER | PATH_TO_A_.dump]
#
# The live install (C:\CollectionMaxxing), the nightly backup folder and CM_BACKUP_DIR are refused (exit 3) unless
# --confirm-live is given. The owner passes it for the real drill; anyone rehearsing uses a copy in a temp folder.
#
# With a folder it takes the newest collectionmaxxing-<time>.dump in <folder>/db (a folder that is itself a "db"
# folder works too). With no argument it uses $CM_BACKUP_DIR. It then:
#   1. checks the dump against the sha256 recorded in its manifest;
#   2. starts ONE fresh throwaway Postgres container (no network, no ports, no volumes, named cm-drill-*) and restores
#      the dump into it;
#   3. runs the migrations check (the newest applied migration must be the one the manifest recorded);
#   4. compares the row count of every table in the manifest with the restored one;
#   5. restores the files archive in the same container and compares the sha256 list with the manifest;
#   6. prints PASS or FAIL for each step with the numbers, then RESULT: PASS|FAIL, and removes its container.
# It never touches the running stack: it does not read or write the live database or its volume, and the only
# container it ever starts, executes in or removes is the one it created.
#
# Exit codes: 0 every check passed, 1 at least one check failed, 2 the drill could not run (no backup, no Docker),
# 3 refused: the path is live data and --confirm-live was not given.
# Settings (environment): CM_BACKUP_DIR, CM_DRILL_IMAGE (postgres:17), CM_DRILL_PREFIX (cm-drill), CM_DRILL_DOCKER (docker).
set -u
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' # Git for Windows: keep /tmp-style paths inside docker arguments as they are

DOCKER="${CM_DRILL_DOCKER:-docker}"
IMAGE="${CM_DRILL_IMAGE:-postgres:17}"
PREFIX="${CM_DRILL_PREFIX:-cm-drill}"
DB_USER=collectionmaxxing
DB_NAME=collectionmaxxing

say() { echo "$*"; }
die() { echo "restore drill: $*" >&2; exit 2; }
failures=0
pass() { say "PASS $*"; }
fail() { say "FAIL $*"; failures=$((failures + 1)); }

# ---- the live-path guard -------------------------------------------------------------------------------------------------------
# The drill only reads the backup, but it must never be pointed at the owner's live install or nightly backup folder by
# accident (a copy-pasted path, a default). Those need --confirm-live. Exit 3 = refused.
confirm_live=0
args=()
for a in "$@"; do
  if [[ "$a" == "--confirm-live" ]]; then confirm_live=1; else args+=("$a"); fi
done
set -- ${args[@]+"${args[@]}"}

# Lower-case, forward slashes, /c/x and /mnt/c/x as c:/x, "." and ".." collapsed, no trailing slash.
cm_norm() {
  local p="${1//\\//}"
  p="${p,,}"
  if [[ "$p" =~ ^/mnt/([a-z])(/.*)?$ ]]; then p="${BASH_REMATCH[1]}:${BASH_REMATCH[2]:-/}"
  elif [[ "$p" =~ ^/([a-z])(/.*)?$ ]]; then p="${BASH_REMATCH[1]}:${BASH_REMATCH[2]:-/}"; fi
  local prefix="" part
  local -a out=()
  [[ "$p" == /* ]] && prefix="/"
  [[ "$p" =~ ^[a-z]:/ ]] && { prefix="${p:0:3}"; p="${p:3}"; }
  local IFS=/
  for part in $p; do
    case "$part" in
      "" | .) ;;
      ..) ((${#out[@]} > 0)) && unset 'out[${#out[@]}-1]' ;;
      *) out+=("$part") ;;
    esac
  done
  local joined="${out[*]:-}"
  echo "${prefix%/}${prefix:+/}${joined}" | sed 's|/$||'
}

# Places that are the owner's live data: the install folder, the usual nightly backup folder, CM_BACKUP_DIR, and anything
# in CM_LIVE_PATHS (";"-separated; restore-drill.ps1 adds the CM_BACKUP_DIR it read from .env).
cm_live_roots() {
  local r
  echo "c:/collectionmaxxing"
  echo "f:/backups/collectionmaxxing"
  [[ -n "${CM_BACKUP_DIR:-}" ]] && cm_norm "$CM_BACKUP_DIR"
  if [[ -n "${CM_LIVE_PATHS:-}" ]]; then
    local IFS=';'
    for r in $CM_LIVE_PATHS; do [[ -n "$r" ]] && cm_norm "$r"; done
  fi
  return 0
}
cm_is_live() {
  local p root
  p="$(cm_norm "$1")"
  while IFS= read -r root; do
    [[ -n "$root" ]] || continue
    [[ "$p" == "$root" || "$p" == "$root"/* ]] && return 0
  done < <(cm_live_roots)
  return 1
}

# ---- find the backup -----------------------------------------------------------------------------------------------------
target="${1:-${CM_BACKUP_DIR:-}}"
[[ -n "$target" ]] || die "give a backup folder or a .dump file, or set CM_BACKUP_DIR"
if cm_is_live "$target"; then
  if ((confirm_live == 0)); then
    echo "restore drill: REFUSED. '$target' is your live install or nightly backup folder." >&2
    echo "  The drill only reads it, but it will not touch live data unless you say so: add --confirm-live" >&2
    echo "  (restore-drill.ps1: -ConfirmLive). To rehearse safely, copy a dump and its manifest to a temp folder first." >&2
    exit 3
  fi
  say "restore drill: live path confirmed with --confirm-live (read-only; the drill runs in its own throwaway container)"
fi
if [[ -f "$target" ]]; then
  dump="$target"
else
  [[ -d "$target" ]] || die "'$target' is not a folder or a file"
  dbdir="$target/db"
  [[ -d "$dbdir" ]] || dbdir="$target"
  newest="$(ls -1 "$dbdir" 2>/dev/null | grep -E '^collectionmaxxing-[0-9]{8}-[0-9]{6}\.dump$' | LC_ALL=C sort | tail -n 1)"
  [[ -n "$newest" ]] || die "no collectionmaxxing-<time>.dump found in $dbdir"
  dump="$dbdir/$newest"
fi
[[ -s "$dump" ]] || die "$dump is missing or empty"
dump_name="$(basename "$dump")"
dump_dir="$(dirname "$dump")"
manifest="${dump%.dump}.manifest.json"
say "restore drill: $dump_name (from $dump_dir)"

command -v "$DOCKER" >/dev/null 2>&1 || die "Docker was not found"
"$DOCKER" info >/dev/null 2>&1 || die "the Docker daemon is not reachable (start Docker Desktop)"

if [[ ! -f "$manifest" ]]; then
  fail "no manifest next to the dump (expected $(basename "$manifest")): there is nothing to compare the restore with"
  say "RESULT: FAIL"
  exit 1
fi

# ---- read the manifest (one value per line; written by cm_write_manifest) ------------------------------------------------
mf_value() { sed -n "s/^ *\"$1\": \"\{0,1\}\([^\",]*\)\"\{0,1\},\{0,1\}\$/\1/p" "$manifest" | head -n 1; }
mf_counts() { sed -n "/\"$1\": {/,/^  }/p" "$manifest" | sed -n 's/^ *"\([A-Za-z0-9_]*\)": \([0-9]*\),\{0,1\}$/\1 \2/p'; }
want_schema="$(mf_value schemaVersion)"
want_dump_sha="$(mf_value dumpSha256)"
archive_name="$(mf_value archiveFile)"
want_archive_sha="$(mf_value archiveSha256)"
[[ -n "$want_schema" && -n "$want_dump_sha" ]] || { fail "the manifest is unreadable (no schemaVersion or dumpSha256)"; say "RESULT: FAIL"; exit 1; }

# ---- step 1: the dump is the one the manifest describes --------------------------------------------------------------------
got_dump_sha="$(sha256sum <"$dump" | cut -d' ' -f1)"
if [[ "$got_dump_sha" == "$want_dump_sha" ]]; then
  pass "dump sha256 matches the manifest"
else
  fail "dump sha256 is $got_dump_sha but the manifest says $want_dump_sha (the dump changed after it was written)"
  say "RESULT: FAIL"
  exit 1
fi

# ---- step 2: a fresh throwaway database -------------------------------------------------------------------------------------
name="$PREFIX-$(date +%s)-$RANDOM"
created=0
cleanup() {
  # Only the one container this run created.
  # -v also removes the anonymous data volume the postgres image creates for this container, so nothing is left behind.
  if ((created == 1)); then "$DOCKER" rm -fv "$name" >/dev/null 2>&1; fi
}
trap cleanup EXIT

# --network none: it cannot reach, or be reached by, anything. No -p, no -v: it cannot touch a port or a volume.
# The password is a random one-off for this container; it protects nothing.
if ! run_err="$("$DOCKER" run -d --name "$name" --label cm-drill=1 --network none \
  -e POSTGRES_USER="$DB_USER" -e POSTGRES_DB="$DB_NAME" -e POSTGRES_PASSWORD="drill-$RANDOM$RANDOM$RANDOM" \
  "$IMAGE" 2>&1 >/dev/null)"; then
  # docker's own words (a registry error, a name clash), not just "could not start".
  say "restore drill: could not start $IMAGE: $(printf '%s' "$run_err" | tr '\r\n' '  ')"
  exit 2
fi
created=1

ready=0
for _ in $(seq 1 90); do
  # Checking TCP on 127.0.0.1 inside the container skips the image's temporary start-up server.
  if "$DOCKER" exec "$name" pg_isready -h 127.0.0.1 -U "$DB_USER" >/dev/null 2>&1; then ready=1; break; fi
  sleep 1
done
((ready == 1)) || { say "restore drill: the throwaway database did not become ready"; exit 2; }

dsql() { "$DOCKER" exec -i "$name" psql -U "$DB_USER" -d "$DB_NAME" -X -A -t -v ON_ERROR_STOP=1 "$@"; }

if "$DOCKER" exec -i "$name" pg_restore -U "$DB_USER" -d "$DB_NAME" --no-owner --no-privileges --exit-on-error <"$dump" 2>"${TMPDIR:-/tmp}/cm-drill-restore.$$.err"; then
  pass "restore: pg_restore finished without errors"
else
  fail "restore: pg_restore failed: $(head -n 3 "${TMPDIR:-/tmp}/cm-drill-restore.$$.err" | tr '\n' ' ')"
fi
rm -f "${TMPDIR:-/tmp}/cm-drill-restore.$$.err"

# ---- step 3: migrations check -----------------------------------------------------------------------------------------------
got_schema="$(dsql -c 'select migration_id from "__EFMigrationsHistory" order by migration_id desc limit 1' 2>/dev/null | head -n 1 | tr -d '\r')"
if [[ -z "$got_schema" ]]; then
  fail "migrations: the restored database has no migration history"
elif [[ "$got_schema" == "$want_schema" ]]; then
  pass "migrations: newest applied is $got_schema, as recorded"
else
  fail "migrations: newest applied is $got_schema but the manifest recorded $want_schema"
fi

# ---- step 4: row counts -----------------------------------------------------------------------------------------------------
# The manifest holds the counts read just before and just after the dump; the dump's own moment lies in between.
before="$(mf_counts tableCountsBefore)"
after="$(mf_counts tableCountsAfter)"
tables="$(printf '%s\n' "$before" | cut -d' ' -f1 | grep . || true)"
if [[ -z "$tables" ]]; then
  fail "table counts: the manifest lists no tables"
fi
for t in $tables; do
  b="$(printf '%s\n' "$before" | awk -v t="$t" '$1 == t { print $2 }')"
  a="$(printf '%s\n' "$after" | awk -v t="$t" '$1 == t { print $2 }')"
  [[ -n "$a" ]] || a="$b"
  lo=$((b < a ? b : a))
  hi=$((b > a ? b : a))
  got="$(dsql -c "select count(*) from public.\"$t\"" 2>/dev/null | head -n 1 | tr -d '\r')"
  if [[ ! "$got" =~ ^[0-9]+$ ]]; then
    fail "table $t: missing from the restored database (expected $b)"
  elif ((lo == hi)); then
    if ((got == lo)); then pass "table $t: restored $got, expected $lo"; else fail "table $t: restored $got, expected $lo"; fi
  else
    if ((got >= lo && got <= hi)); then pass "table $t: restored $got, expected between $lo and $hi"; else fail "table $t: restored $got, expected between $lo and $hi"; fi
  fi
done

# ---- step 5: files, by hash list ------------------------------------------------------------------------------------------------
want_files="$(sed -n 's/^ *{"path": "\(.*\)", "sha256": "\([0-9a-f]\{64\}\)"},\{0,1\}$/\2  \1/p' "$manifest" | sed -e 's/\\"/"/g' -e 's/\\\\/\\/g' | LC_ALL=C sort -k2)"
want_count="$(printf '%s' "$want_files" | grep -c . || true)"
root="$(dirname "$dump_dir")"
archive=""
for candidate in "$root/files/$archive_name" "$dump_dir/$archive_name"; do
  [[ -n "$archive_name" && -f "$candidate" ]] && { archive="$candidate"; break; }
done
if [[ -z "$archive" ]]; then
  fail "files: the archive $archive_name from the manifest was not found next to the dump"
else
  got_archive_sha="$(sha256sum <"$archive" | cut -d' ' -f1)"
  if [[ "$got_archive_sha" != "$want_archive_sha" ]]; then
    fail "files: archive sha256 is $got_archive_sha but the manifest says $want_archive_sha"
  elif ! "$DOCKER" exec -i "$name" sh -c 'mkdir -p /restore && tar -xzf - -C /restore' <"$archive"; then
    fail "files: the archive could not be unpacked"
  else
    got_files="$("$DOCKER" exec "$name" sh -c "cd /restore && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum" 2>/dev/null |
      sed 's|^\([0-9a-f]*\)  \./|\1  |' | LC_ALL=C sort -k2)"
    got_count="$(printf '%s' "$got_files" | grep -c . || true)"
    differing="$(diff <(printf '%s\n' "$want_files") <(printf '%s\n' "$got_files") | grep -c '^[<>]' || true)"
    if ((differing == 0)); then
      pass "files: $got_count restored, $want_count expected, every hash matches"
    else
      fail "files: $got_count restored, $want_count expected, $differing list entries differ (missing, extra or changed files)"
    fi
  fi
fi

# ---- result ---------------------------------------------------------------------------------------------------------------------
if ((failures == 0)); then
  say "RESULT: PASS"
  exit 0
fi
say "RESULT: FAIL ($failures check(s) failed)"
exit 1
