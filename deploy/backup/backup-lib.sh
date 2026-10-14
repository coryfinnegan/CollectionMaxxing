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

# cm_backup_run: one backup. Dump -> verify -> files archive -> prune (only if everything worked) -> heartbeat.
# Returns 0 on success, 1 on failure. A failed run never prunes anything.
cm_backup_run() {
  local dir="${BACKUP_DIR:-/backups}" files_src="${FILES_DIR:-/data/files}" ts ok=1
  ts="$(cm_timestamp)"
  mkdir -p "$dir/db" "$dir/files" || ok=0

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
