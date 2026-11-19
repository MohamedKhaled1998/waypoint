#!/usr/bin/env bash
# Restore an encrypted dump into $TARGET_DATABASE (default: waypoint_restore, never the live one unless you say so).
# Usage: restore.sh <dump.sql.gz.enc>
set -euo pipefail

dump="${1:?usage: restore.sh <dump.sql.gz.enc>}"
: "${MYSQL_HOST:=db}"
: "${MYSQL_USER:=root}"
: "${MYSQL_PWD:?set MYSQL_PWD}"
: "${BACKUP_PASSPHRASE:?set BACKUP_PASSPHRASE}"
: "${TARGET_DATABASE:=waypoint_restore}"
export MYSQL_PWD

mysql -h "$MYSQL_HOST" -u "$MYSQL_USER" -e "CREATE DATABASE IF NOT EXISTS \`$TARGET_DATABASE\`"
# Fail closed: a dump that names a database (CREATE DATABASE / USE) could write somewhere other than the target.
# Dumps from backup.sh never do; if one does, abort before anything reaches MySQL.
tmp="$(mktemp)"; trap 'rm -f "$tmp"' EXIT
openssl enc -d -aes-256-cbc -pbkdf2 -pass env:BACKUP_PASSPHRASE -in "$dump" | gunzip > "$tmp"
if grep -qiE '^(CREATE DATABASE|USE )' "$tmp"; then
  echo "restore: dump selects a database itself; refusing" >&2; exit 1
fi
mysql -h "$MYSQL_HOST" -u "$MYSQL_USER" "$TARGET_DATABASE" < "$tmp"
echo "restore: $(basename "$dump") -> $TARGET_DATABASE"
