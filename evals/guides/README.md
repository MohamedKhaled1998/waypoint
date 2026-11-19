# Guide prompt evals

Outside the build: not run by `dotnet test` or Jest. Run after changing `GuidePrompt.System` or the Guide model.

    node evals/guides/run.mjs [fitness|study|red-flag]

Needs the `claude` CLI logged in (no API key). Feeds the prototype's three cases (`prototypes/guides/cases`) to the
server's real prompt, read from `server/Waypoint.Api/Coach/Guides.cs`, then a weekly refresh, and checks the replies
against the server's Guide rules plus the red-flag rules. Transcripts land in `evals/guides/out/`.
