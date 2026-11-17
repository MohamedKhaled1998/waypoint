# Issue tracker

Waypoint is tracked in ClickUp, space **Mansour**, list **Waypoint**. Keep personal details out of tickets: describe product decisions, not the user's own Aspirations.

Use `NODE_USE_ENV_PROXY=1 node --no-warnings ~/Workspace/sandcastle/clickup.ts <command> --space Mansour` (see the script header for commands).

## Wayfinding operations

- **Map**: a top-level task in the Waypoint list, tagged `wayfinder:map`. Its description is the map body.
- **Tickets**: subtasks of the map, each tagged with one `wayfinder:<type>` (`research`, `prototype`, `grilling`, `task`).
- **Blocking**: ClickUp's native "waiting on" dependency (`clickup.ts depend <ticket> --on <blocker>`).
- **Claim**: assign the ticket to the dev driving the map.
- **Frontier**: open subtasks of the map with no open blockers and no assignee.
- **Resolve**: post the answer as a comment, set the status to closed/complete, and append a line to the map's Decisions so far.
- **Research findings**: Markdown on a throwaway `research/<name>` branch of this repo, linked from the ticket. Never merged.
