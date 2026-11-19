# Stage 2: a small EU VPS

## When to move

Move when any of these is true (spec: Architecture, Hosting):

- someone else will use Waypoint;
- the PC being off makes the Coach noticeably late more than about once a week;
- the developer will be away longer than the two weeks of cached Actions.

Database, backups and logs stay in the EU.

## Provision

1. Hetzner Cloud, CPX12-class, location Falkenstein/Nuremberg (Germany) or Helsinki. Add your SSH public key at creation; enable disk encryption on the backup target and keep `BACKUP_PASSPHRASE` off the box too.
2. Point a DNS name (`waypoint.example.com`) at the server.
3. Harden:
   - Unattended security upgrades: `apt install unattended-upgrades` then `dpkg-reconfigure -plow unattended-upgrades`.
   - SSH by key only: in `/etc/ssh/sshd_config.d/10-waypoint.conf` set `PasswordAuthentication no`, `PermitRootLogin prohibit-password`, `KbdInteractiveAuthentication no`; `systemctl reload ssh`. Test a second session before closing the first.
   - Firewall open only on 443 and SSH: `ufw default deny incoming; ufw allow OpenSSH; ufw allow 443/tcp; ufw enable` (plus the Hetzner Cloud Firewall with the same two rules). Port 8080 and MySQL are not published to the internet.
4. Install Docker (`apt install docker.io docker-compose-v2`).

## Deploy the same Compose file

1. Clone the repo, create `.env` (as in README, plus `BACKUP_PASSPHRASE`, `BACKUP_DIR`) and `secrets/firebase-service-account.json`.
2. Caddy terminates TLS. Add a `docker-compose.override.yml` on the server (not committed):

   ```yaml
   services:
     api:
       ports: !reset []
     caddy:
       image: caddy:2
       restart: unless-stopped
       ports: ["443:443", "80:80"]
       command: caddy reverse-proxy --from waypoint.example.com --to api:8080
       volumes: [caddy-data:/data]
   volumes:
     caddy-data:
   ```

   Port 80 is only for Caddy's certificate challenge; close it in the firewall if you use the DNS challenge instead.
3. `docker compose up -d --build`.
4. Restore the latest dump if you are moving data (`backup-and-restore.md`), then point the app at `https://waypoint.example.com` (`EXPO_PUBLIC_API_URL`) and rebuild the APK.

## Watch it

Add a free external uptime check (for example UptimeRobot or Better Stack free tier) on `https://waypoint.example.com/` every 5 minutes, alerting to your email or phone.

## Left for the developer

- Set the Anthropic console hard limit to $10/month and an alert at $8.
- Enable disk encryption on the host (PC now, VPS volume encryption at stage 2).
- Run the restore drill before real data.
