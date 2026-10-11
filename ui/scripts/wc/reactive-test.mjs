// Reactive core regression tests (design-system/test/reactive.test.ts), run in Chromium through a Vite dev server.
// Usage (from ui/): node scripts/wc/reactive-test.mjs   (PORT=… picks the dev server port, default 5201)
import { chromium } from "playwright";
import { createServer } from "vite";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../design-system", import.meta.url));
const port = Number(process.env.PORT ?? 5201);
const server = await createServer({ root, configFile: false, logLevel: "error", server: { port, strictPort: true } });
await server.listen();
const browser = await chromium.launch();
const page = await browser.newPage();
page.on("pageerror", (e) => console.log("page error:", e.message));
await page.goto(`http://localhost:${port}/test/index.html`);
const results = await page.waitForFunction(() => window.__results, null, { timeout: 20000 }).then((h) => h.jsonValue());
await browser.close();
await server.close();
let failed = 0;
for (const r of results) {
  console.log(`${r.ok ? "PASS" : "FAIL"} ${r.name}`);
  if (!r.ok) (failed++, console.log("     " + r.error.split("\n").slice(0, 3).join("\n     ")));
}
console.log(`${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
