// Accessibility tree of the app (mock backend) against the committed baselines in scripts/aria-baseline/: the roles,
// names and states a screen reader is given, in the places axe can't judge (is this the right name, is the row marked
// selected, did focus and the announcement follow F8). It doesn't run a screen reader: see ACCESSIBILITY.md.
//   npm run check:aria, or node scripts/shell-aria.mjs [appUrl|port] [--update] (--update rewrites the baselines:
//   review the diff, a changed name is what a screen reader user will hear).
import { mkdirSync, readFileSync, writeFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";
import { baseUrlOr, startApp } from "./lib/dev-server.mjs";

const update = process.argv.includes("--update");
const baselines = fileURLToPath(new URL("./aria-baseline/", import.meta.url));
const { base, stop } = await baseUrlOr(process.argv.slice(2).find((a) => !a.startsWith("--")), (o) => startApp({ frozen: true, ...o }));
const browser = await chromium.launch();
let failed = 0;

const fail = (name, detail) => {
  failed++;
  console.log(`FAIL ${name}\n${detail}`);
};

const lineDiff = (expected, actual) => {
  const was = new Set(expected.split("\n"));
  const now = new Set(actual.split("\n"));
  return [...[...was].filter((l) => !now.has(l)).map((l) => `  - ${l}`), ...[...now].filter((l) => !was.has(l)).map((l) => `  + ${l}`)].join("\n");
};

/** Compares the snapshots of `locators` (joined, in order) with the baseline `name`. */
const snapshot = async (name, ...locators) => {
  const parts = [];
  for (const l of locators) parts.push(await l.ariaSnapshot());
  const actual = parts.join("\n") + "\n";
  const file = `${baselines}${name}.yml`;
  if (update) {
    mkdirSync(baselines, { recursive: true });
    writeFileSync(file, actual);
    console.log(`wrote ${name}.yml`);
    return;
  }
  if (!existsSync(file)) return fail(name, "  no baseline: run with --update");
  const expected = readFileSync(file, "utf8").replaceAll("\r\n", "\n");
  if (expected === actual) console.log(`ok   ${name}`);
  else fail(name, lineDiff(expected, actual) || "  same lines in another order");
};

const check = (name, ok, detail) => (ok ? console.log(`ok   ${name}`) : fail(name, `  ${detail}`));

/** A page on the mock with a fixed locale, zone and size, so the names and the virtualized rows are the same everywhere. */
const open = async (query = "") => {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, reducedMotion: "reduce", locale: "en-US", timezoneId: "UTC" });
  const page = await ctx.newPage();
  await page.goto(`${base}?mock&fast${query}`);
  await page.waitForSelector(".tree-row");
  // the build failure and the previous results arrive after the first rows: wait for the complete suite and its project
  await page.getByRole("progressbar", { name: /^Suite: 8 failed/ }).waitFor();
  await page.getByRole("alert").filter({ hasText: "Build failed" }).waitFor();
  return { page, close: () => ctx.close() };
};

{
  const { page, close } = await open();
  const tree = page.getByRole("tree", { name: "Test tree" });
  const filter = page.getByRole("radiogroup", { name: "Status filter" });
  await snapshot("shell", page.getByRole("banner"), page.getByRole("navigation", { name: "Main" }));
  await snapshot("tree", filter, tree);

  await filter.getByRole("radio", { name: /^Failed/ }).click();
  await page.locator(".tree-row").first().waitFor();
  await snapshot("tree-failed", filter, tree);
  await filter.getByRole("radio", { name: /^All/ }).click();

  await page.keyboard.press("F8");
  const heading = page.getByRole("tabpanel", { name: "Test details" }).getByRole("heading", { level: 2 });
  await heading.waitFor();
  const failure = page.getByRole("group", { name: "Failure" });
  await failure.waitFor();
  await snapshot("failure-detail", page.getByRole("navigation", { name: "Location" }), heading, failure);

  const selected = page.getByRole("treeitem", { selected: true });
  const focus = await page.evaluate(() => ({ role: document.activeElement?.getAttribute("role"), active: document.activeElement?.getAttribute("aria-activedescendant") }));
  check("F8: focus is on the tree", focus.role === "tree", `focus is on role=${focus.role}`);
  check("F8: the active row is the selected row", focus.active === (await selected.getAttribute("id")), `aria-activedescendant=${focus.active}`);
  const spoken = await page.locator('[aria-live="polite"]').allTextContents();
  check("F8: the failure is announced", spoken.some((t) => /^Failure \d+ of \d+: \S+ — /.test(t)), JSON.stringify(spoken));

  await page.locator("body").click({ position: { x: 1, y: 1 } });
  await page.keyboard.press("?");
  const dialog = page.getByRole("dialog");
  await dialog.waitFor();
  await snapshot("shortcuts-dialog", dialog);
  await close();
}

{
  const { page, close } = await open();
  await page.getByRole("link", { name: "Settings" }).click();
  const settings = page.getByRole("region", { name: "Settings" });
  await settings.getByRole("heading", { name: "Settings" }).waitFor();
  await snapshot("settings", settings);
  await close();
}

await browser.close();
await stop();
console.log(update ? "baselines updated" : failed ? `${failed} FAILED` : "all aria snapshots match");
process.exit(failed ? 1 : 0);
