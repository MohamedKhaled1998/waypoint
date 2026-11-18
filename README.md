# Waypoint

An Android app with an LLM Coach. Spec: `docs/spec/v1.md`; terms: `GLOSSARY.md`.

## Run the server

Needs Docker. Create `.env` next to `docker-compose.yml` (never commit it):

| Variable | Meaning |
| --- | --- |
| `MYSQL_PASSWORD` | Password of the `waypoint` database user |
| `MYSQL_ROOT_PASSWORD` | MySQL root password |
| `JWT_KEY` | Random string of 32+ characters that signs access tokens |
| `GOOGLE_WEB_CLIENT_ID` | Google OAuth **Web** client ID the ID tokens are issued for |

```
docker compose up --build
```

The API listens on port 8080 and applies EF Core migrations on start. Locally, for the API outside Docker, set
`ConnectionStrings__Waypoint`, `Jwt__Key` and `Google__ClientIds__0` (or use `dotnet user-secrets`).

Reach it from the phone over Tailscale: install Tailscale on the PC and the phone, then use `http://<pc-tailnet-name>:8080`.

## Server tests

`dotnet test server/Waypoint.slnx` (needs Docker: the API tests run in-process against MySQL in Testcontainers).

## Build and sideload the app

Needs Node, JDK 17 and the Android SDK. `android/` is generated (Continuous Native Generation) and never committed.

1. In Google Cloud, create an OAuth **Web** client (its ID is `GOOGLE_WEB_CLIENT_ID`) and an **Android** client for package `app.waypoint` with your signing key's SHA-1.
2. Create `app/.env` (not committed):
   ```
   EXPO_PUBLIC_API_URL=http://<pc-tailnet-name>:8080
   EXPO_PUBLIC_GOOGLE_WEB_CLIENT_ID=<the Web client ID>
   ```
3. `npm ci --prefix app`
4. Development build: `cd app && npx expo prebuild --platform android && npx expo run:android`, then `npm start --prefix app`.
5. Release APK: `cd app && npx expo prebuild --platform android --clean && cd android && ./gradlew assembleRelease`; sideload `android/app/build/outputs/apk/release/app-release.apk` with `adb install -r <apk>`.

App checks: `npm run --prefix app typecheck`, `npm test --prefix app`.
