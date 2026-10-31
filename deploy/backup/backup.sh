#!/usr/bin/env bash
# Entry point of the backup image.
#   backup.sh            run forever: first catch up (back up now if the newest successful backup is older than the
#                        last scheduled run, or there is none), then wait for BACKUP_SCHEDULE ("M H * * *", daily),
#                        back up, repeat
#   backup.sh once       back up now, then exit with the result (use this for a manual run)
#   backup.sh health     the container's HEALTHCHECK: exit 0 when Postgres answers and the newest successful backup
#                        is younger than CM_HEALTH_MAX_AGE_HOURS (30) or one is running; exit 1 with the reason otherwise
# Settings come from the environment: PGHOST, PGDATABASE, PGUSER, PGPASSWORD (libpq), BACKUP_SCHEDULE,
# BACKUP_DIR (/backups), FILES_DIR (/data/files), CM_HEARTBEAT_URL (empty = no pings), CM_KEEP_DAILY/WEEKLY/MONTHLY.
set -u
# shellcheck source=backup-lib.sh
. "$(dirname "$0")/backup-lib.sh"

case "${1:-daemon}" in
  once)
    cm_backup_run
    exit $?
    ;;
  health)
    cm_health_check
    exit $?
    ;;
  daemon)
    schedule="${BACKUP_SCHEDULE:-0 3 * * *}"
    cm_seconds_until "$schedule" >/dev/null || { cm_log "BACKUP_SCHEDULE '$schedule' is not a daily 'M H * * *' schedule"; exit 2; }
    cm_log "scheduled: '$schedule' (container time zone ${TZ:-UTC})"
    rm -f "$(cm_run_marker)" # a marker left by a run that was killed in this container's earlier life
    cm_catch_up "$schedule"
    while true; do
      wait_s="$(cm_seconds_until "$schedule")"
      cm_log "next backup in ${wait_s}s"
      sleep "$wait_s"
      cm_backup_run || cm_log "backup failed; will try again at the next scheduled time"
      sleep 61 # step past the scheduled minute so one run is never repeated
    done
    ;;
  *)
    echo "usage: backup.sh [once|health]" >&2
    exit 2
    ;;
esac
