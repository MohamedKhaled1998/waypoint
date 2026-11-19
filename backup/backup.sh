#!/usr/bin/env bash
# One encrypted dump of $MYSQL_DATABASE into $BACKUP_DIR, then prune dumps older than $BACKUP_RETENTION_DAYS.
# The dump has no CREATE DATABASE or USE line, so a restore lands only in the database it is told to.
# Output: waypoint-<UTC timestamp>.sql.gz.enc (AES-256-CBC, PBKDF2; passphrase in $BACKUP_PASSPHRASE).
set -euo pipefail

: "${MYSQL_HOST:=db}"
: "${MYSQL_DATABASE:=waypoint}"
: "${MYSQL_USER:=root}"
: "${MYSQL_PWD:?set MYSQL_PWD}"
: "${BACKUP_PASSPHRASE:?set BACKUP_PASSPHRASE}"
: "${BACKUP_DIR:=/backups}"
: "${BACKUP_RETENTION_DAYS:=30}"
export MYSQL_PWD

mkdir -p "$BACKUP_DIR"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
out="$BACKUP_DIR/waypoint-$stamp.sql.gz.enc"
tmp="$out.partial"
trap 'rm -f "$tmp"' EXIT

mysqldump -h "$MYSQL_HOST" -u "$MYSQL_USER" --single-transaction --routines --no-tablespaces \
    "$MYSQL_DATABASE" \
  | gzip -9 \
  | BACKUP_PASSPHRASE="$BACKUP_PASSPHRASE" openssl enc -aes-256-cbc -pbkdf2 -salt -pass env:BACKUP_PASSPHRASE \
  > "$tmp"

[ -s "$tmp" ] || { echo "backup: empty dump" >&2; exit 1; }
mv "$tmp" "$out"
trap - EXIT

find "$BACKUP_DIR" -maxdepth 1 -name 'waypoint-*.sql.gz.enc' -mtime +"$BACKUP_RETENTION_DAYS" -delete
echo "backup: wrote $(basename "$out")"
