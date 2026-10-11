// Markdown coverage summary (overall + per package) from Vitest's json-summary report, for $GITHUB_STEP_SUMMARY.
// Usage (from ui/, after `npm run test:coverage`): node scripts/coverage-summary.mjs >> "$GITHUB_STEP_SUMMARY"
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const ui = fileURLToPath(new URL("..", import.meta.url));
const file = path.join(ui, "coverage", "coverage-summary.json");
let summary;
try {
  summary = JSON.parse(readFileSync(file, "utf8"));
} catch {
  console.log("### UI unit test coverage\n\nNo coverage report (coverage/coverage-summary.json) was produced.");
  process.exit(0);
}

const METRICS = ["lines", "statements", "functions", "branches"];
const packages = [
  ["App (src/)", path.join(ui, "src") + path.sep],
  ["Design system (design-system/src/)", path.join(ui, "design-system", "src") + path.sep],
];

const add = (acc, m) => {
  for (const k of METRICS) {
    acc[k].covered += m[k].covered;
    acc[k].total += m[k].total;
  }
};
const empty = () => Object.fromEntries(METRICS.map((k) => [k, { covered: 0, total: 0 }]));
const rows = packages.map(([name]) => [name, empty()]);
for (const [f, m] of Object.entries(summary)) {
  if (f === "total") continue;
  const i = packages.findIndex(([, dir]) => f.startsWith(dir));
  if (i >= 0) add(rows[i][1], m);
}
const pct = ({ covered, total }) => (total ? `${((covered / total) * 100).toFixed(2)} %` : "–");

console.log("### UI unit test coverage\n");
console.log(`| Package | ${METRICS.map((m) => m[0].toUpperCase() + m.slice(1)).join(" | ")} |`);
console.log(`|---|${METRICS.map(() => "---:").join("|")}|`);
for (const [name, m] of rows) console.log(`| ${name} | ${METRICS.map((k) => pct(m[k])).join(" | ")} |`);
console.log(`| **Total** | ${METRICS.map((k) => `**${pct(summary.total[k])}**`).join(" | ")} |`);
console.log("\nThresholds: lines / statements / functions ≥ 85 % overall and per package (branches reported only).");
