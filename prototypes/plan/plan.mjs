// PROTOTYPE — throwaway. The Coach proposes a Plan from an Interview hand-off, in the
// terminal, through the `claude` CLI (your Claude login, no API key).
//
//   node prototypes/plan/plan.mjs                                  # sample hand-off
//   node prototypes/plan/plan.mjs prototypes/plan/out/handoff-….json # from a real Interview
//
// Optional: MODEL=haiku (default sonnet), START=YYYY-MM-DD (default next Monday).
// After each proposal it checks the Plan against the hand-off: Time budget share, Slots,
// Milestone dates. Reply to push back; "/accept" or "/quit" ends.
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { createInterface } from "node:readline/promises";

const here = (p) => fileURLToPath(new URL(p, import.meta.url));
const handoffPath = process.argv[2] ?? here("./sample-handoff.json");
const handoff = JSON.parse(readFileSync(handoffPath, "utf8"));
const model = process.env.MODEL ?? "sonnet";
const system = readFileSync(here("./coach-plan.prompt.md"), "utf8");

const day = (d) => new Date(`${d}T00:00:00Z`);
const iso = (d) => d.toISOString().slice(0, 10);
const addDays = (d, n) => new Date(d.getTime() + n * 86400000);
const today = iso(new Date());
const start = process.env.START ?? iso(addDays(day(today), ((8 - day(today).getUTCDay()) % 7) || 7));

const sessionId = randomUUID();
let turns = 0;
function coach(message) {
  const sessionArgs = turns++ === 0 ? ["--session-id", sessionId] : ["--resume", sessionId];
  const out = execFileSync(
    "claude",
    ["-p", ...sessionArgs, "--system-prompt", system, "--tools", "", "--model", model,
     "--output-format", "json", message],
    { encoding: "utf8", stdio: ["ignore", "pipe", "inherit"], maxBuffer: 1 << 24 },
  );
  return JSON.parse(out).result;
}

// Checks the Plan JSON against the hand-off and prints what it finds.
function check(plan) {
  const { user, aspiration: a } = handoff;
  const notes = [];
  const flag = (ok, text) => notes.push(`${ok ? "  ok " : "  !! "} ${text}`);
  const dayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
  const mins = (t) => { const [h, m] = t.split(":").map(Number); return h * 60 + m; };
  const slots = Object.fromEntries(user.slots.map((s) => [s.label, s]));

  const ms = plan.milestones ?? [];
  flag(ms.length >= 3 && ms.length <= 6, `${ms.length} Milestones (guide 3–6)`);
  if (ms.length) {
    const firstGap = (day(ms[0].date) - day(start)) / 86400000;
    flag(firstGap >= 14 && firstGap <= 28, `first Milestone ${ms[0].date}, ${firstGap} days after start ${start} (guide 14–28)`);
    const last = ms.at(-1).date;
    if (a.hoped_for_date) flag(last <= a.hoped_for_date, `last Milestone ${last} vs Hoped-for ${a.hoped_for_date}`);
    if (a.settle_for_date) flag(ms.every((m) => m.date <= a.settle_for_date), `all Milestones by Settle-for ${a.settle_for_date}`);
    flag(ms.every((m) => m.reached_when?.trim()), `every Milestone has a "reached when" line`);
  }
  const ids = new Set(ms.map((m) => m.id));
  flag((plan.tasks ?? []).every((t) => ids.has(t.milestone)), `every task belongs to a Milestone`);

  const window = plan.first_two_weeks ?? [];
  const end = iso(addDays(day(start), 13));
  const perSlotDay = {};
  const perWeek = [0, 0];
  for (const e of window) {
    const s = slots[e.slot];
    if (!s) { flag(false, `${e.date} "${e.action}": unknown Slot "${e.slot}"`); continue; }
    if (!s.days.includes(dayNames[day(e.date).getUTCDay()])) flag(false, `${e.date} (${dayNames[day(e.date).getUTCDay()]}) is not a "${e.slot}" day`);
    if (e.date < start || e.date > end) flag(false, `${e.date} is outside the first two weeks (${start}..${end})`);
    const key = `${e.date} ${e.slot}`;
    perSlotDay[key] = (perSlotDay[key] ?? 0) + e.minutes;
    perWeek[(day(e.date) - day(start)) / 86400000 < 7 ? 0 : 1] += e.minutes;
  }
  for (const [key, used] of Object.entries(perSlotDay)) {
    const s = slots[key.slice(11)];
    const room = mins(s.end) - mins(s.start);
    if (used > room) flag(false, `${key}: ${used} min in a ${room}-min Slot`);
  }
  if (!notes.some((n) => n.includes("Slot") && n.startsWith("  !!"))) flag(true, `every Action fits its Slot's day and length`);
  const share = (a.share_hours_per_week ?? user.time_budget_hours_per_week) * 60;
  perWeek.forEach((m, i) => {
    const pct = Math.round((m / share) * 100);
    flag(pct >= 55 && pct <= 75, `week ${i + 1}: ${m} of ${share} min share = ${pct}% (ramp guide 60–70%)`);
  });

  console.log("\n--- check against the hand-off ---");
  console.log(notes.join("\n"));
  console.log("--- first two weeks ---");
  for (const e of [...window].sort((x, y) => (x.date + x.slot).localeCompare(y.date + y.slot)))
    console.log(`  ${e.date} ${dayNames[day(e.date).getUTCDay()]}  ${e.slot.padEnd(20)} ${String(e.minutes).padStart(3)} min  ${e.action}`);
  console.log("");
}

function show(reply) {
  const json = reply.match(/```json\s*([\s\S]*?)```/);
  console.log(`\nCoach: ${json ? reply.replace(json[0], "[Plan JSON hidden, checked below]") : reply}`);
  if (!json) return;
  try { check(JSON.parse(json[1])); } catch (e) { console.log(`\n[could not check the Plan JSON: ${e.message}]\n`); }
}

const started = Date.now();
show(coach(`Today is ${today}. The Plan starts ${start}.\n\nInterview hand-off:\n\`\`\`json\n${JSON.stringify(handoff, null, 2)}\n\`\`\``));
const rl = createInterface({ input: process.stdin, output: process.stdout });
while (true) {
  const message = await rl.question("You: ");
  if (["/accept", "/quit"].includes(message.trim())) break;
  show(coach(message));
}
rl.close();
console.log(`\n[${turns} Coach turns, ${Math.round((Date.now() - started) / 60000)} min, model ${model}]`);
