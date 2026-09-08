import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import { createInterface } from "node:readline/promises";
import { createCollectorClient } from "../src/collector-client.mjs";

const args = process.argv.slice(2);
const get = name => { const index = args.indexOf(name); return index < 0 ? undefined : args[index + 1]; };
const file = process.env.AGENT_COLLECTOR_CONFIG || path.resolve(import.meta.dirname, "../collector.json");
if (fs.existsSync(file)) throw new Error("collector.json already exists. Revoke the old device and move that file aside before enrolling again.");
const url = get("--url"); const tunnelId = get("--tunnel-id"); const name = get("--name") || os.hostname();
if (!url) throw new Error("Usage: npm run device:enroll -- --url INGESTION_URL --tunnel-id ID.CLUSTER --name NAME");
const client = createCollectorClient({ url, tunnelId });
// Prepare directory before consuming the one-time pairing code.
const input = createInterface({ input: process.stdin, output: process.stdout });
const code = process.env.AGENT_PAIR_CODE || await input.question("Pairing code from the central dashboard: ");
input.close();
const enrollment = await client.send("/v1/devices/register", "POST", { code: code.trim(), name });
fs.writeFileSync(file, JSON.stringify({ url, tunnelId, deviceId: enrollment.deviceId, token: enrollment.token, name }, null, 2), { flag: "wx", mode: 0o600 });
console.log("Device paired. Credentials saved in ignored collector.json. Start with npm run collector.");
