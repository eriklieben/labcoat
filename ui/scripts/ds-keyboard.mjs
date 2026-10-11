// DS: keyboard tests of the gallery primitives (menu, tabs, dialog, split pane, switch, segmented, tooltip).
// Usage: npm run check:keyboard, or node scripts/ds-keyboard.mjs [galleryUrl|-] [screenshotDir] (without a URL it
// starts the gallery itself).
import { chromium } from "playwright";
import { baseUrlOr, startGallery } from "./lib/dev-server.mjs";

const { base, stop } = await baseUrlOr(process.argv[2], startGallery);
const shots = process.argv[3];
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const errors = [];
page.on("pageerror", (e) => errors.push(e.message));
await page.goto(`${base}?theme=aspire-dark`);
await page.waitForSelector(".g-section");
let failed = 0;
const check = (name, cond, extra = "") => {
  console.log(`${cond ? "PASS" : "FAIL"} ${name}${extra ? ` (${extra})` : ""}`);
  if (!cond) failed++;
};
const active = () => page.evaluate(() => {
  const a = document.activeElement;
  return { tag: a?.tagName, role: a?.getAttribute("role"), text: a?.textContent?.trim().slice(0, 40), label: a?.getAttribute("aria-label"), id: a?.id };
});
const key = (k) => page.keyboard.press(k);

// ---- switch ----
const sw = page.getByRole("switch", { name: "Sounds" });
await sw.focus();
await key("Space");
check("switch: Space toggles on", await sw.isChecked());
await key("Space");
check("switch: Space toggles off", !(await sw.isChecked()));

// ---- segmented ----
const mode = page.getByRole("radiogroup", { name: "Mode" }).nth(1); // gallery demo (0 = header)
await mode.getByRole("radio", { name: "System" }).focus();
await key("ArrowRight");
check("segmented: ArrowRight selects next", (await mode.getByRole("radio", { name: "Light" }).getAttribute("aria-checked")) === "true");
check("segmented: focus moved", (await active()).text === "Light");
await key("End");
check("segmented: End selects last", (await mode.getByRole("radio", { name: "Dark" }).getAttribute("aria-checked")) === "true");
await key("ArrowRight");
check("segmented: wraps to first", (await mode.getByRole("radio", { name: "System" }).getAttribute("aria-checked")) === "true");
check("segmented: roving tabindex (1 tabbable)", (await mode.locator('[tabindex="0"]').count()) === 1);

// ---- tabs ----
const tablist = page.getByRole("tablist", { name: "Detail views" });
await tablist.getByRole("tab", { name: /Test details/ }).focus();
await key("ArrowRight");
check("tabs: ArrowRight selects Coverage", (await tablist.getByRole("tab", { name: /Coverage/ }).getAttribute("aria-selected")) === "true");
check("tabs: panel follows", (await page.locator("#gallery-tabs-panel-coverage").count()) === 1);
await key("End");
check("tabs: End selects History", (await tablist.getByRole("tab", { name: /History/ }).getAttribute("aria-selected")) === "true");
await key("Home");
check("tabs: Home selects first", (await tablist.getByRole("tab", { name: /Test details/ }).getAttribute("aria-selected")) === "true");
await key("Tab");
check("tabs: Tab moves into the panel", (await active()).role === "tabpanel");

// ---- menu ----
const menuBtn = page.getByRole("button", { name: "Actions", exact: true });
await menuBtn.focus();
await key("Enter");
await page.waitForSelector(".lc-menu");
check("menu: Enter opens, aria-expanded", (await menuBtn.getAttribute("aria-expanded")) === "true");
check("menu: focus on first item", (await active()).text?.startsWith("Run"), JSON.stringify(await active()));
await key("ArrowDown");
check("menu: ArrowDown → Debug", (await active()).text?.startsWith("Debug"));
await key("End");
check("menu: End → last item", (await active()).text?.startsWith("Delete history"));
await key("ArrowDown");
check("menu: wraps to first", (await active()).text?.startsWith("Run"));
await key("o");
check("menu: type-ahead 'o' → Open in editor", (await active()).text?.startsWith("Open in editor"));
await key("ArrowUp");
check("menu: ArrowUp → Debug", (await active()).text?.startsWith("Debug"));
if (shots) await page.screenshot({ path: `${shots}/kbd-menu-open.png` });
await key("Escape");
// closed = gone or leaving (an 80 ms exit fade keeps it inert in the DOM until it is removed)
check("menu: Escape closes", (await page.locator(".lc-menu:not([inert])").count()) === 0);
check("menu: focus back on button", (await active()).text?.startsWith("Actions"));
await key("ArrowUp");
await page.waitForSelector(".lc-menu");
check("menu: ArrowUp opens at last item", (await active()).text?.startsWith("Delete history"));
await key("Home");
await key("Enter");
check("menu: Enter activates + closes", (await page.locator(".lc-menu:not([inert])").count()) === 0 && (await page.getByText("last: Run ·").count()) === 1);
check("menu: focus returned after select", (await active()).text?.startsWith("Actions"));
// radio item
await key("Enter");
await page.waitForSelector(".lc-menu");
check("menu: action menu opens on the first item", (await active()).text?.startsWith("Run"));
// a closing menu stays in the DOM (inert) while it fades out, so target the open one
await page.locator(".lc-menu:not([inert])").getByRole("menuitemradio", { name: "Duration" }).focus();
await key("Space");
check("menu: Space selects radio", (await page.getByText("sort: duration").count()) === 1);
// clipped container: the menu must render outside the overflow:hidden bar
await page.getByRole("button", { name: "Live", exact: true }).click();
await page.waitForSelector(".lc-menu");
const box = await page.locator(".lc-menu").boundingBox();
const bar = await page.locator(".g-clip-demo").boundingBox();
const visible = await page.evaluate(([x, y1, y2]) => [y1, y2].every((y) => !!document.elementFromPoint(x, y)?.closest(".lc-menu")), [box.x + 12, box.y + 6, box.y + box.height - 6]);
const inBar = await page.evaluate(() => !!document.querySelector(".g-clip-demo .lc-menu"));
check("menu: escapes overflow:hidden (not inside the clipping bar, top and bottom edge visible)", visible && !inBar && box.height > 200, `${Math.round(box.y)}+${Math.round(box.height)}px, bar at ${Math.round(bar.y)}`);
if (shots) await page.screenshot({ path: `${shots}/kbd-menu-clip.png` });
await key("Tab");
check("menu: Tab closes", (await page.locator(".lc-menu:not([inert])").count()) === 0);

// ---- tooltip ----
const tipBtn = page.getByRole("button", { name: "Top", exact: true });
await page.getByRole("button", { name: "Bottom", exact: true }).focus();
await key("Shift+Tab");
await page.waitForSelector(".lc-tooltip");
const tipId = await page.locator(".lc-tooltip").getAttribute("id");
check("tooltip: shows on keyboard focus with aria-describedby", (await tipBtn.getAttribute("aria-describedby")) === tipId);
await key("Escape");
check("tooltip: Escape hides", (await page.locator(".lc-tooltip").count()) === 0);
await page.getByRole("button", { name: "Right", exact: true }).hover();
await page.waitForTimeout(700);
check("tooltip: shows on hover after delay", (await page.locator(".lc-tooltip").count()) === 1);
await page.mouse.move(5, 5);
await page.waitForTimeout(300);
check("tooltip: hides on leave", (await page.locator(".lc-tooltip").count()) === 0);

// ---- dialog ----
const opener = page.getByRole("button", { name: "Keyboard shortcuts" });
await opener.focus();
await key("Enter");
await page.waitForSelector("dialog.lc-dialog[open]");
check("dialog: opens modal with name", (await page.getByRole("dialog", { name: "Keyboard shortcuts" }).count()) === 1);
const first = await active();
check("dialog: focus inside", await page.evaluate(() => !!document.activeElement?.closest("dialog")), JSON.stringify(first));
for (let i = 0; i < 4; i++) await key("Tab");
check("dialog: Tab stays inside (trap)", await page.evaluate(() => !!document.activeElement?.closest("dialog")));
await key("Shift+Tab");
await key("Shift+Tab");
await key("Shift+Tab");
check("dialog: Shift+Tab stays inside", await page.evaluate(() => !!document.activeElement?.closest("dialog")));
if (shots) await page.screenshot({ path: `${shots}/kbd-dialog.png` });
await key("Escape");
check("dialog: Escape closes", (await page.locator("dialog.lc-dialog[open]").count()) === 0); // the closed dialog fades out (100 ms) before removal
check("dialog: focus returns to opener", (await active()).text === "Keyboard shortcuts");
// palette
await page.getByRole("button", { name: /Command palette/ }).click();
await page.waitForSelector("dialog.lc-dialog[open]");
check("palette: input focused", (await active()).role === "combobox");
await page.keyboard.type("watch");
await key("ArrowDown");
check("palette: filters", (await page.locator(".g-palette-item").count()) === 1);
if (shots) await page.screenshot({ path: `${shots}/kbd-palette.png` });
await key("Escape");
// backdrop click
await page.getByRole("button", { name: "Keyboard shortcuts" }).click();
await page.waitForSelector("dialog.lc-dialog[open]");
await page.mouse.click(10, 450);
check("dialog: backdrop click closes", (await page.locator("dialog.lc-dialog[open]").count()) === 0);

// ---- split pane ----
const sep = page.getByRole("separator", { name: "Resize test tree" });
await sep.focus();
const v0 = Number(await sep.getAttribute("aria-valuenow"));
await key("ArrowRight");
const v1 = Number(await sep.getAttribute("aria-valuenow"));
check("split: ArrowRight grows by 16", v1 === v0 + 16, `${v0} → ${v1}`);
await key("Shift+ArrowLeft");
check("split: Shift+ArrowLeft shrinks by 80", Number(await sep.getAttribute("aria-valuenow")) === v1 - 80);
await key("Home");
check("split: Home → min", (await sep.getAttribute("aria-valuenow")) === (await sep.getAttribute("aria-valuemin")));
await key("End");
check("split: End → max", (await sep.getAttribute("aria-valuenow")) === (await sep.getAttribute("aria-valuemax")));
await key("Enter");
check("split: Enter collapses to min", (await sep.getAttribute("aria-valuenow")) === (await sep.getAttribute("aria-valuemin")));
await key("Enter");
check("split: Enter restores", (await sep.getAttribute("aria-valuenow")) === (await sep.getAttribute("aria-valuemax")));
check("split: size persisted via onResizeEnd", (await page.getByText(/persisted width: \d+ px/).textContent()).includes(await sep.getAttribute("aria-valuenow")));
await sep.scrollIntoViewIfNeeded();
const sb = await sep.boundingBox();
await page.mouse.move(sb.x + 4, sb.y + sb.height / 2);
await page.mouse.down();
await page.mouse.move(sb.x - 96, sb.y + sb.height / 2, { steps: 5 });
await page.mouse.up();
const v2 = Number(await sep.getAttribute("aria-valuenow"));
check("split: pointer drag resizes", Math.abs(v2 - (Number(await sep.getAttribute("aria-valuemax")) - 100)) <= 2, `${v2}`);
const sepH = page.getByRole("separator", { name: "Resize output panel" });
await sepH.focus();
const h0 = Number(await sepH.getAttribute("aria-valuenow"));
await key("ArrowUp");
check("split (column, sized second): ArrowUp grows the bottom pane", Number(await sepH.getAttribute("aria-valuenow")) === h0 + 16);

check("no page errors", errors.length === 0, errors.join("; "));
await browser.close();
await stop();
console.log(failed ? `${failed} FAILED` : "all keyboard checks passed");
process.exit(failed ? 1 : 0);
