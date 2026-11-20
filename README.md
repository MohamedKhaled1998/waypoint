# Waypoint

An Android app with an LLM Coach. Spec: `docs/spec/v1.md`; terms: `GLOSSARY.md`.

## Set up

Run the setup wizard once (and again whenever something changes; it keeps values already saved):

```
scripts/setup.sh
```

It walks you through creating the Firebase project, Google sign-in clients, the Anthropic key with its $10 limit and $8 alert, and the Tailscale address. It writes `.env`, `app/.env`, `app/google-services.json` and `secrets/firebase-service-account.json`, all gitignored. The sections below say what each value is.

## Run the server

Needs Docker. `.env` next to `docker-compose.yml` (never commit it):

| Variable | Meaning |
| --- | --- |
| `MYSQL_PASSWORD` | Password of the `waypoint` database user |
| `MYSQL_ROOT_PASSWORD` | MySQL root password |
| `JWT_KEY` | Random string of 32+ characters that signs access tokens |
| `GOOGLE_WEB_CLIENT_ID` | Google OAuth **Web** client ID the ID tokens are issued for |
| `ANTHROPIC_API_KEY` | Anthropic API key for the Coach |
| `BACKUP_PASSPHRASE` | Encrypts the nightly dumps; keep a copy off this machine |
| `BACKUP_DIR` | Host path on off-machine storage that receives the dumps (30-day retention) |
| `Firebase__ServiceAccountFile` | Path of the Firebase service-account JSON the API sends pushes with. Compose sets it to `/run/secrets/firebase-service-account.json` and mounts `./secrets` there read-only; put the file at `secrets/firebase-service-account.json` (gitignored). The API refuses to start without a readable file |

```
docker compose up --build
```

**Coach on your Claude subscription (development only).** To test without paying for API calls, add to `.env`:

```
COACH_USE_CLAUDE_CLI=true
CLAUDE_CODE_OAUTH_TOKEN=<from `claude setup-token`>
```

and rebuild (`docker compose up --build`). The image then includes the `claude` CLI and every Coach call runs through it on your subscription and its usage limits; `ANTHROPIC_API_KEY` is ignored. Only for you testing locally: never with real users.

The API listens on port 8080 and applies EF Core migrations on start. Locally, for the API outside Docker, set
`ConnectionStrings__Waypoint`, `Jwt__Key`, `Google__ClientIds__0` and `Firebase__ServiceAccountFile` (or use `dotnet user-secrets`).

Reach it from the phone over Tailscale: install Tailscale on the PC and the phone, then use `http://<pc-tailnet-name>:8080`.

## Operations

Backups and the restore drill: `docs/ops/backup-and-restore.md`. Moving to a VPS: `docs/ops/stage-2-vps.md`.

## Server tests

`dotnet test server/Waypoint.slnx` (needs Docker: the API tests run in-process against MySQL in Testcontainers).

## Build and sideload the app

Needs Node, JDK 17 and the Android SDK. `android/` is generated (Continuous Native Generation) and never committed.

1. In Google Cloud, create an OAuth **Web** client (its ID is `GOOGLE_WEB_CLIENT_ID`) and an **Android** client for package `app.waypoint` with your signing key's SHA-1.
2. In Firebase, add an Android app for package `app.waypoint` and save its `google-services.json` as `app/google-services.json` (gitignored; the build uses it when present).
3. Create `app/.env` (not committed):
   ```
   EXPO_PUBLIC_API_URL=http://<pc-tailnet-name>:8080
   EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID=<the Web client ID>
   ```
4. `npm ci --prefix app`
5. Development build: `cd app && npx expo prebuild --platform android && npx expo run:android`, then `npm start --prefix app`.
6. Release APK: `cd app && npx expo prebuild --platform android --clean && cd android && ./gradlew assembleRelease`; sideload `android/app/build/outputs/apk/release/app-release.apk` with `adb install -r <apk>`.

App checks: `npm run --prefix app typecheck`, `npm test --prefix app`.
