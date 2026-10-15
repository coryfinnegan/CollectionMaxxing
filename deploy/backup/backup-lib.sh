#!/usr/bin/env bash
# Functions for the nightly backup. Sourced by backup.sh and by the tests; has no side effects when sourced.
# Needs bash 4+ and GNU date.

cm_log() { echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) backup: $*"; }

cm_timestamp() { date -u +%Y%m%d-%H%M%S; }

# cm_retention_keep: reads backup names on stdin (anything containing YYYYMMDD-HHMMSS) and prints the ones to KEEP.
# Policy (the newest backup of each period survives):
#   daily   the most recent CM_KEEP_DAILY (14) distinct days that have a backup
#   weekly  the most recent CM_KEEP_WEEKLY (8) distinct ISO weeks
#   monthly the most recent CM_KEEP_MONTHLY (12) distinct months
# A name survives if any rule keeps it. The newest backup is always a daily keeper. Names without a timestamp are ignored.
cm_retention_keep() {
  local daily="${CM_KEEP_DAILY:-14}" weekly="${CM_KEEP_WEEKLY:-8}" monthly="${CM_KEEP_MONTHLY:-12}"
  local -A seen_d=() seen_w=() seen_m=()
  local nd=0 nw=0 nm=0 name stamp day week month keep
  # Sort newest first by the embedded timestamp.
  while IFS= read -r name; do
    [[ "$name" =~ ([0-9]{8}-[0-9]{6}) ]] && echo "${BASH_REMATCH[1]} $name"
  done | LC_ALL=C sort -r | while read -r stamp name; do
    day="${stamp:0:8}"
    month="${stamp:0:6}"
    week="$(date -d "${day:0:4}-${day:4:2}-${day:6:2}" +%G-%V)"
    keep=0
    if [[ -z "${seen_d[$day]:-}" ]]; then seen_d[$day]=1; nd=$((nd + 1)); ((nd <= daily)) && keep=1; fi
    if [[ -z "${seen_w[$week]:-}" ]]; then seen_w[$week]=1; nw=$((nw + 1)); ((nw <= weekly)) && keep=1; fi
    if [[ -z "${seen_m[$month]:-}" ]]; then seen_m[$month]=1; nm=$((nm + 1)); ((nm <= monthly)) && keep=1; fi
    ((keep == 1)) && echo "$name"
  done
  return 0
}

# cm_retention_delete: names on stdin -> the timestamped names that are NOT kept. Never includes the newest.
cm_retention_delete() {
  local all kept newest
  all="$(cat)"
  [[ -z "$all" ]] && return 0
  kept="$(printf '%s\n' "$all" | cm_retention_keep)"
  newest="$(printf '%s\n' "$all" | while IFS= read -r n; do [[ "$n" =~ ([0-9]{8}-[0-9]{6}) ]] && echo "${BASH_REMATCH[1]} $n"; done | LC_ALL=C sort -r | head -n 1 | cut -d' ' -f2-)"
  printf '%s\n' "$all" | while IFS= read -r n; do
    [[ "$n" =~ [0-9]{8}-[0-9]{6} ]] || continue
    [[ "$n" == "$newest" ]] && continue
    grep -qxF -- "$n" <<<"$kept" || echo "$n"
  done
  return 0
}

# cm_prune DIR PREFIX EXT: deletes old backups in DIR whose names are exactly PREFIX-YYYYMMDD-HHMMSS.EXT.
cm_prune() {
  local dir="$1" prefix="$2" ext="$3" name
  local pattern="^${prefix}-[0-9]{8}-[0-9]{6}\.${ext//./\\.}\$"
  # shellcheck disable=SC2012
  ls -1 "$dir" 2>/dev/null | grep -E "$pattern" | cm_retention_delete | while IFS= read -r name; do
    [[ "$name" =~ $pattern ]] || continue # belt and braces: never remove anything that is not ours
    rm -f -- "${dir:?}/$name"
    # A dump's manifest lives and dies with it.
    [[ "$ext" == "dump" ]] && rm -f -- "${dir:?}/${name%.dump}.manifest.json"
    cm_log "pruned $name"
  done
  return 0
}

# cm_heartbeat ok|fail: pings CM_HEARTBEAT_URL (success) or CM_HEARTBEAT_URL/fail (failure). Empty URL pings nothing.
# A failed ping is logged but never fails the backup.
cm_heartbeat() {
  local url="${CM_HEARTBEAT_URL:-}"
  [[ -z "$url" ]] && return 0
  [[ "${1:-ok}" == "fail" ]] && url="${url%/}/fail"
  curl -fsS -m 10 --retry 3 -o /dev/null "$url" || cm_log "heartbeat ping failed"
  return 0
}

# ---- the manifest: what the restore drill compares a restored backup against ---------------------------------------
# It holds counts, hashes, file names and times, nothing else: no row data and no file content. The only questions
# asked of the database are "how many rows does each table have" and "which migration is the newest".

# cm_table_counts: prints "table|rows" for every table in the public schema.
cm_table_counts() {
  local out line
  out="$(psql -X -A -t -F'|' -v ON_ERROR_STOP=1 -c "select table_name, (xpath('/row/c/text()', query_to_xml(format('select count(*) as c from %I.%I', table_schema, table_name), false, true, '')))[1]::text::bigint from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE' order by 1")" || return 1
  [[ -n "$out" ]] || return 1
  while IFS= read -r line; do
    [[ "$line" =~ ^[A-Za-z0-9_]+\|[0-9]+$ ]] || return 1 # anything but "name|number" is refused, never copied
  done <<<"$out"
  printf '%s\n' "$out"
}

# cm_schema_version: prints the newest EF Core migration id applied to the database.
cm_schema_version() {
  local v
  v="$(psql -X -A -t -v ON_ERROR_STOP=1 -c 'select "MigrationId" from "__EFMigrationsHistory" order by "MigrationId" desc limit 1')" || return 1
  [[ "$v" =~ ^[A-Za-z0-9_]+$ ]] || return 1
  printf '%s\n' "$v"
}

# cm_file_hashes DIR: prints "sha256  relative/path" for every file under DIR, sorted by path.
cm_file_hashes() {
  (cd "$1" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum | sed 's|^\([0-9a-f]*\) [ *]\./|\1  |')
}

# cm_json_counts: reads "name|count" lines on stdin, prints them as JSON members.
cm_json_counts() {
  local first=1 name count
  while IFS='|' read -r name count; do
    [[ -z "$name" ]] && continue
    [[ "$name" =~ ^[A-Za-z0-9_]+$ && "$count" =~ ^[0-9]+$ ]] || return 1
    ((first == 1)) || printf ',\n'
    printf '    "%s": %s' "$name" "$count"
    first=0
  done
  ((first == 1)) || printf '\n'
  return 0
}

# cm_write_manifest DUMP ARCHIVE COUNTS_BEFORE COUNTS_AFTER SCHEMA_VERSION: writes DUMP's manifest next to it.
# The file list is read from the archive itself, so it describes exactly what a restore will produce.
# The row counts are read just before and just after the dump; the dump's own moment lies between, so the drill
# accepts anything in that range (at 03:00 the two are normally equal).
cm_write_manifest() {
  local dump="$1" archive="$2" before="$3" after="$4" schema="$5"
  local out="${dump%.dump}.manifest.json" tmpx hashes stamp
  stamp="$(basename "$dump" .dump)"
  stamp="${stamp#collectionmaxxing-}"
  tmpx="$(dirname "$dump")/.manifest-tmp-$$"
  rm -rf -- "${tmpx:?}"
  mkdir -p "$tmpx" || return 1
  if ! tar -xzf "$archive" -C "$tmpx" || ! hashes="$(cm_file_hashes "$tmpx")"; then
    rm -rf -- "${tmpx:?}"
    return 1
  fi
  rm -rf -- "${tmpx:?}"
  {
    printf '{\n'
    printf '  "manifestVersion": 1,\n'
    printf '  "createdAt": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    printf '  "timestamp": "%s",\n' "$stamp"
    printf '  "schemaVersion": "%s",\n' "$schema"
    printf '  "dumpFile": "%s",\n' "$(basename "$dump")"
    printf '  "dumpSha256": "%s",\n' "$(sha256sum <"$dump" | cut -d' ' -f1)"
    printf '  "dumpBytes": %s,\n' "$(wc -c <"$dump" | tr -d ' ')"
    printf '  "archiveFile": "%s",\n' "$(basename "$archive")"
    printf '  "archiveSha256": "%s",\n' "$(sha256sum <"$archive" | cut -d' ' -f1)"
    printf '  "tableCountsBefore": {\n'
    printf '%s\n' "$before" | cm_json_counts
    printf '  },\n'
    printf '  "tableCountsAfter": {\n'
    printf '%s\n' "$after" | cm_json_counts
    printf '  },\n'
    printf '  "fileCount": %s,\n' "$(printf '%s' "$hashes" | grep -c . || true)"
    printf '  "files": [\n'
    printf '%s\n' "$hashes" | grep . | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' \
      -e 's|^\([0-9a-f]\{64\}\)  \(.*\)$|    {"path": "\2", "sha256": "\1"},|' | sed '$ s/,$//' || true
    printf '  ]\n'
    printf '}\n'
  } >"$out.partial" || { rm -f "$out.partial"; return 1; }
  mv -f "$out.partial" "$out"
}

# cm_backup_run: one backup. Counts -> dump -> verify -> files archive -> manifest -> prune (only if everything worked)
# -> heartbeat. Returns 0 on success, 1 on failure. A failed run never prunes anything.
cm_backup_run() {
  local dir="${BACKUP_DIR:-/backups}" files_src="${FILES_DIR:-/data/files}" ts ok=1
  local before="" after="" schema=""
  ts="$(cm_timestamp)"
  mkdir -p "$dir/db" "$dir/files" || ok=0

  if ((ok == 1)); then
    if ! before="$(cm_table_counts)" || ! schema="$(cm_schema_version)"; then
      cm_log "could not read the table counts and schema version for the manifest; backup FAILED"
      ok=0
    fi
  fi

  local dump="$dir/db/collectionmaxxing-$ts.dump"
  if ((ok == 1)) && pg_dump -Fc -f "$dump.partial" && [[ -s "$dump.partial" ]] && pg_restore --list "$dump.partial" >/dev/null; then
    mv -f "$dump.partial" "$dump"
    cm_log "database dumped to db/$(basename "$dump")"
  else
    rm -f "$dump.partial"
    cm_log "database dump FAILED"
    ok=0
  fi

  local archive="$dir/files/files-$ts.tar.gz"
  if ((ok == 1)); then
    if tar -C "$files_src" -czf "$archive.partial" . && [[ -s "$archive.partial" ]]; then
      mv -f "$archive.partial" "$archive"
      cm_log "files archived to files/$(basename "$archive")"
    else
      rm -f "$archive.partial"
      cm_log "files archive FAILED"
      ok=0
    fi
  fi

  if ((ok == 1)); then
    if after="$(cm_table_counts)" && cm_write_manifest "$dump" "$archive" "$before" "$after" "$schema"; then
      cm_log "manifest written to db/$(basename "${dump%.dump}").manifest.json"
    else
      rm -f "${dump%.dump}.manifest.json.partial"
      cm_log "manifest FAILED"
      ok=0
    fi
  fi

  if ((ok == 1)); then
    cm_prune "$dir/db" "collectionmaxxing" "dump"
    cm_prune "$dir/files" "files" "tar.gz"
    cm_heartbeat ok
    return 0
  fi
  cm_heartbeat fail
  return 1
}

# cm_seconds_until "M H * * *": seconds until the next HH:MM (today if still ahead, else tomorrow). Only daily schedules are supported.
cm_seconds_until() {
  local schedule="$1" m h rest now target
  read -r m h rest <<<"$schedule"
  [[ "$m" =~ ^[0-9]+$ && "$h" =~ ^[0-9]+$ && "$rest" == "* * *" ]] || return 1
  ((m < 60 && h < 24)) || return 1
  now="$(date +%s)"
  target="$(date -d "today $(printf '%02d:%02d' "$h" "$m")" +%s)"
  ((target <= now)) && target="$(date -d "tomorrow $(printf '%02d:%02d' "$h" "$m")" +%s)"
  echo $((target - now))
}
