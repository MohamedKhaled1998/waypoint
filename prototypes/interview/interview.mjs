// PROTOTYPE — throwaway. Runs the Coach Interview in the terminal through the
// `claude` CLI, so it uses your Claude login or CLAUDE_CODE_OAUTH_TOKEN, no API key.
//
//   node prototypes/interview/interview.mjs "become more fit"
//
// Optional: MODEL=haiku to compare models (default sonnet). Type your answers; "/quit" ends.
// Tools are off. Claude Code keeps the session in its own history (needed for --resume).
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { createInterface } from "node:readline/promises";

const aspiration = process.argv.slice(2).join(" ");
if (!aspiration) {
  console.error('Usage: node interview.mjs "<your Aspiration>"');
  process.exit(1);
}
const model = process.env.MODEL ?? "sonnet";
const system = readFileSync(fileURLToPath(new URL("./coach-interview.prompt.md", import.meta.url)), "utf8");
const sessionId = randomUUID();
const rl = createInterface({ input: process.stdin, output: process.stdout });
let coachTurns = 0;
const started = Date.now();

function coach(message) {
  const sessionArgs = coachTurns === 0 ? ["--session-id", sessionId] : ["--resume", sessionId];
  const out = execFileSync(
    "claude",
    ["-p", ...sessionArgs, "--system-prompt", system, "--tools", "", "--model", model,
     "--output-format", "json", message],
    { encoding: "utf8", stdio: ["ignore", "pipe", "inherit"] },
  );
  return JSON.parse(out).result;
}

let message = `My Aspiration: ${aspiration}`;
while (true) {
  const reply = coach(message);
  coachTurns++;
  console.log(`\nCoach (${coachTurns}): ${reply}\n`);
  const handoff = reply.match(/```json\s*([\s\S]*?)```/);
  if (handoff) {
    // PROTOTYPE, wipe me: the hand-off the Plan runner reads.
    const dir = fileURLToPath(new URL("../plan/out/", import.meta.url));
    mkdirSync(dir, { recursive: true });
    const file = `${dir}handoff-${Date.now()}.json`;
    writeFileSync(file, handoff[1]);
    console.log(`[hand-off saved: ${file}]\n[next: node prototypes/plan/plan.mjs ${file}]\n`);
  }
  message = await rl.question("You: ");
  if (message.trim() === "/quit") break;
}
rl.close();
console.log(`\n[${coachTurns} Coach turns, ${Math.round((Date.now() - started) / 60000)} min, model ${model}]`);
