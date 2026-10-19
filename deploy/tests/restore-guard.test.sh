#!/usr/bin/env bash
# Tests for the live-path guard in scripts/restore-drill.sh. Needs bash only: the guard runs before the drill looks for
# Docker, so a fake "docker" that records every call proves nothing was started. Prints "PASS name" or "FAIL name: why".
set -u
here="$(cd "$(dirname "$0")" && pwd)"
drill="$here/../../scripts/restore-drill.sh"
pass=0
fail=0
ok() { pass=$((pass + 1)); echo "PASS $1"; }
bad() { fail=$((fail + 1)); echo "FAIL $1: $2"; }
check() { if [[ "$2" == "$3" ]]; then ok "$1"; else bad "$1" "expected [$2] but got [$3]"; fi; }
contains() { if [[ "$3" == *"$2"* ]]; then ok "$1"; else bad "$1" "expected to find [$2] in: $(printf '%s' "$3" | tail -n 6)"; fi; }

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
calls="$tmp/docker-calls"
fake="$tmp/fake-docker"
printf '#!/usr/bin/env bash\necho "$*" >> "%s"\nexit 1\n' "$calls" >"$fake"
chmod +x "$fake"
export CM_DRILL_DOCKER="$fake"
unset CM_BACKUP_DIR CM_LIVE_PATHS
run_drill() { out="$(bash "$drill" "$@" 2>&1)"; rc=$?; }

run_drill 'C:\CollectionMaxxing\repo'
check "guard: the live install folder is refused with exit 3" 3 "$rc"
contains "guard: the refusal names the flag to use" "--confirm-live" "$out"

run_drill 'C:\CollectionMaxxing\repo\..\secrets'
check "guard: a .. path into the live folder is refused" 3 "$rc"

run_drill '/c/CollectionMaxxing/backups'
check "guard: the Git Bash spelling of the live folder is refused" 3 "$rc"

run_drill 'c:/COLLECTIONMAXXING'
check "guard: the live folder is refused whatever the case or slash" 3 "$rc"

run_drill 'F:\Backups\CollectionMaxxing'
check "guard: the usual nightly backup folder on F: is refused" 3 "$rc"

run_drill 'F:\Backups\CollectionMaxxing\db\collectionmaxxing-20261013-030000.dump'
check "guard: a dump inside the live backup folder is refused" 3 "$rc"

CM_BACKUP_DIR='G:\MyOwnBackups' run_drill
check "guard: no argument means CM_BACKUP_DIR, which is live: refused" 3 "$rc"

CM_BACKUP_DIR='G:\MyOwnBackups' run_drill 'G:\MyOwnBackups\db'
check "guard: a custom CM_BACKUP_DIR is protected too" 3 "$rc"

CM_LIVE_PATHS='/some/live/place;D:\other' run_drill '/some/live/place/db'
check "guard: CM_LIVE_PATHS adds protected places" 3 "$rc"

check "guard: no refusal ever reached Docker" "no calls" "$([[ -e "$calls" ]] && cat "$calls" || echo 'no calls')"

run_drill "$tmp/synthetic"
check "guard: an ordinary temp folder is not refused (it fails later: not found, exit 2)" 2 "$rc"

run_drill "$tmp/CollectionMaxxing-copy/db"
check "guard: a similar name outside the live folders is not refused" 2 "$rc"

run_drill 'C:\CollectionMaxxing\repo' --confirm-live
check "guard: --confirm-live lets the live folder through the guard (then fails on the missing folder, exit 2)" 2 "$rc"
contains "guard: with the flag the drill says it was confirmed" "confirmed" "$out"

run_drill --confirm-live 'F:\Backups\CollectionMaxxing'
check "guard: the flag works before the path too" 2 "$rc"

echo "SUMMARY passed=$pass failed=$fail"
((fail == 0))
