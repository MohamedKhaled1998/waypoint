# Backups and restore

The `backup` service in `docker-compose.yml` writes one encrypted dump of the `waypoint` database every night (03:00 UTC by default, `BACKUP_HOUR_UTC`) to `BACKUP_DIR` and deletes dumps older than 30 days. Dumps are `mysqldump | gzip | openssl enc -aes-256-cbc -pbkdf2`, named `waypoint-<UTC timestamp>.sql.gz.enc`.

## Set up

In `.env`:

- `BACKUP_PASSPHRASE`: a long random string. **Keep a copy outside this machine** (a password manager); without it the dumps are unreadable.
- `BACKUP_DIR`: host path that is *not* this machine's disk: a mounted NAS or cloud drive (rclone mount, Syncthing folder to another device, and so on). The data stays in the EU (spec: Data, privacy and operations).

Deleting an Aspiration or account rolls off backups after 30 days; there are no single-user restores.

## Restore

Into a fresh database, never over the live one first:

```
docker compose run --rm --no-deps -v "$BACKUP_DIR":/backups backup \
  env TARGET_DATABASE=waypoint_restore /backup/restore.sh /backups/waypoint-<timestamp>.sql.gz.enc
```

Check `waypoint_restore` (row counts, latest Check-in). To go live: stop `api`, drop `waypoint`, rerun with `TARGET_DATABASE=waypoint`, start `api`. On a new host: install the stack (see `stage-2-vps.md`), start only `db`, restore the newest dump, start the rest.

## Restore drill (once, before real data)

```
docker compose up -d db
docker compose run --rm --no-deps backup /backup/restore-drill.sh
```

The drill creates a throwaway database, backs it up, checks the dump is not readable without the passphrase, restores it into a second database, compares the rows, checks that a 31-day-old dump is pruned, and drops both databases. It prints `drill: OK`. Then do one manual restore of a real nightly dump into `waypoint_restore` as above.
