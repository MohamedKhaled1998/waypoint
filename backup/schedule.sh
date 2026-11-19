#!/usr/bin/env bash
# Runs backup.sh every night at $BACKUP_HOUR_UTC (default 03) and keeps going if one run fails.
set -uo pipefail
: "${BACKUP_HOUR_UTC:=03}"
while true; do
  now=$(date -u +%s)
  next=$(date -u -d "today ${BACKUP_HOUR_UTC}:00" +%s)
  [ "$next" -le "$now" ] && next=$(date -u -d "tomorrow ${BACKUP_HOUR_UTC}:00" +%s)
  sleep $((next - now))
  /backup/backup.sh || echo "backup: FAILED" >&2
done
