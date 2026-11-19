#!/usr/bin/env bash
# Restore drill against a throwaway database: seeds a table, backs it up, restores into a fresh database,
# compares row counts, and drops both. Run it where backup.sh runs:
#   docker compose run --rm --no-deps backup /backup/restore-drill.sh   (needs the db service up)
set -euo pipefail
: "${MYSQL_HOST:=db}"
: "${MYSQL_PWD:?set MYSQL_PWD}"
export MYSQL_PWD MYSQL_HOST MYSQL_USER=root
src="drill_src_$$"; dst="drill_dst_$$"; dir="$(mktemp -d)"
cleanup() { mysql -h "$MYSQL_HOST" -uroot -e "DROP DATABASE IF EXISTS \`$src\`; DROP DATABASE IF EXISTS \`$dst\`"; rm -rf "$dir"; }
trap cleanup EXIT

mysql -h "$MYSQL_HOST" -uroot -e "CREATE DATABASE \`$src\`; USE \`$src\`;
  CREATE TABLE t (id INT PRIMARY KEY, note VARCHAR(40)); INSERT INTO t VALUES (1,'a'),(2,'b'),(3,'c');"

MYSQL_DATABASE="$src" BACKUP_DIR="$dir" BACKUP_PASSPHRASE="${BACKUP_PASSPHRASE:-drill-passphrase}" /backup/backup.sh
dump="$(ls "$dir"/waypoint-*.sql.gz.enc)"
# The dump must not be readable without the passphrase.
if gunzip -c "$dump" >/dev/null 2>&1; then echo "drill: dump is not encrypted" >&2; exit 1; fi

# Change the source after the backup: a restore that touched it would put the old rows back.
mysql -h "$MYSQL_HOST" -uroot -e "INSERT INTO \`$src\`.t VALUES (4,'d')"

MYSQL_DATABASE="$src" TARGET_DATABASE="$dst" BACKUP_PASSPHRASE="${BACKUP_PASSPHRASE:-drill-passphrase}" /backup/restore.sh "$dump"
got="$(mysql -h "$MYSQL_HOST" -uroot -N -e "SELECT COUNT(*), GROUP_CONCAT(note ORDER BY id) FROM \`$dst\`.t")"
[ "$got" = $'3\ta,b,c' ] || { echo "drill: restored data differs: $got" >&2; exit 1; }

[ "$(mysql -h "$MYSQL_HOST" -uroot -N -e "SELECT COUNT(*) FROM \`$src\`.t")" = 4 ] \
  || { echo "drill: restore changed the source database" >&2; exit 1; }

# A dump that names a database is refused and writes nothing.
printf 'USE `%s`;\nDELETE FROM t;\n' "$src" | gzip | openssl enc -aes-256-cbc -pbkdf2 -salt -pass pass:x > "$dir/hostile.enc"
if BACKUP_PASSPHRASE=x TARGET_DATABASE="$dst" /backup/restore.sh "$dir/hostile.enc" 2>/dev/null; then
  echo "drill: dump naming a database was accepted" >&2; exit 1; fi
[ "$(mysql -h "$MYSQL_HOST" -uroot -N -e "SELECT COUNT(*) FROM \`$src\`.t")" = 4 ] \
  || { echo "drill: hostile dump changed the source" >&2; exit 1; }

# Retention: a dump older than 30 days goes, a fresh one stays.
touch -d '31 days ago' "$dir/waypoint-old.sql.gz.enc"
BACKUP_DIR="$dir" MYSQL_DATABASE="$src" BACKUP_PASSPHRASE=x /backup/backup.sh >/dev/null
[ ! -e "$dir/waypoint-old.sql.gz.enc" ] || { echo "drill: old dump not pruned" >&2; exit 1; }
[ -e "$dump" ] || { echo "drill: fresh dump pruned" >&2; exit 1; }
echo "drill: OK"
