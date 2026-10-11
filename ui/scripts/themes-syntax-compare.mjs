// Syntax colors side by side: the same C# snippet highlighted by highlight.js, rendered with Labcoat's theme tokens
// (design-system code block + the app's tinted source lines) next to each family's official theme stylesheet.
//   node scripts/themes-syntax-compare.mjs <officialCssDir> <outDir>
// <officialCssDir> holds <family>-<light|dark>.css (an official highlight.js theme, or one generated from the
// family's original editor theme). LC_FAMILIES=a,b limits the families.
import { chromium } from "playwright";
import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { join } from "node:path";
import { FAMILIES } from "./theme-families.mjs";

const require = createRequire(import.meta.url);
const hljs = require("highlight.js/lib/core");
hljs.registerLanguage("csharp", require("highlight.js/lib/languages/csharp"));

const [officialDir, out = "/tmp/syntax-compare"] = process.argv.slice(2);
mkdirSync(out, { recursive: true });
const ds = new URL("../design-system/src/", import.meta.url).pathname;
const appCss = readFileSync(new URL("../src/styles.css", import.meta.url), "utf8");
const themeCss = [...readFileSync(join(ds, "styles.css"), "utf8").matchAll(/@import\s+"\.\/(themes\/[a-z0-9-]+\.css|tokens\/tokens\.css|components\/code-block\.css)"/g)]
  .map((m) => readFileSync(join(ds, m[1]), "utf8"))
  .join("\n");
// the app's hljs + source-line rules (src/styles.css), extracted by selector
const appRules = [...appCss.matchAll(/(^|\n)([^{}\n]*(?:\.hljs|\.src-line|\.src-text|\.source-code)[^{}]*)\{([^{}]*)\}/g)].map((m) => `${m[2]}{${m[3]}}`).join("\n");

const SNIPPET = `/// <summary>Order tests.</summary>
[TestClass]
public sealed class OrderServiceTests
{
    private const int Retries = 3;

    [TestMethod]
    [DataRow("sent", 2.5)]
    public async Task PlaceOrder_SendsEmail(string state, double total)
    {
        var order = new Order { Id = 42, Paid = true, Note = null };
        // act
        await Task.Delay(900);
        Assert.AreEqual("sent", $"queued {order.Id}", "Confirmation email was not dispatched");
    }
}`;
const html = hljs.highlight(SNIPPET, { language: "csharp" }).value;
const lines = html.split("\n");
const RANGE = [7, 14]; // 1-based lines shown as the selected test's method range
const ERR = 13;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1400, height: 600 }, deviceScaleFactor: 1 });
for (const family of FAMILIES)
  for (const mode of ["light", "dark"]) {
    const officialPath = officialDir && join(officialDir, `${family}-${mode}.css`);
    const official = officialPath && existsSync(officialPath) ? readFileSync(officialPath, "utf8") : "";
    const srcLines = lines
      .map((l, i) => {
        const n = i + 1;
        const cls = ["src-line", n >= RANGE[0] && n <= RANGE[1] && "in-range", n === ERR && "err-line"].filter(Boolean).join(" ");
        return `<div class="${cls}"><span class="src-ln">${n}</span><span class="src-text">${l || " "}</span></div>`;
      })
      .join("");
    const covLines = lines
      .map((l, i) => `<div class="src-line ${["cov", "uncov", "partial"][i % 3]}"><span class="src-ln">${i + 1}</span><span class="src-text">${l || " "}</span></div>`)
      .join("");
    await page.setContent(`<!doctype html><html data-theme-family="${family}" data-theme="${mode}"><head><meta charset="utf-8"><style>
${themeCss}
${appRules}
body{margin:0;padding:16px;background:var(--lc-bg-page);color:var(--lc-fg);font:13px/1.4 system-ui,sans-serif}
.grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}
h2{margin:0 0 6px;font-size:12px;font-weight:600}
.lc-code{font-size:12px}
.source-code{padding:6px 0;border:1px solid var(--lc-console-border);border-radius:8px;font-size:12px;line-height:20px}
.src-ln{display:inline-block;width:28px;padding-right:8px;color:var(--lc-console-muted);text-align:right}
.src-line.in-range{background:color-mix(in srgb, var(--lc-accent) 9%, var(--lc-console-bg))}
.src-line.err-line{background:color-mix(in srgb, var(--lc-status-failed) 16%, var(--lc-console-bg))}
.official{all:initial}
</style></head><body>
<div class="grid">
<div><h2>Labcoat ${family} ${mode}: code block</h2><div class="lc-code"><pre class="lc-code-pre"><code class="hljs">${lines.map((l) => `<span class="lc-code-line">${l || " "}</span>`).join("")}</code></pre></div></div>
<div><h2>Labcoat: method range + failing line</h2><div class="source-code">${srcLines}</div></div>
<div><h2>Labcoat: coverage tints (covered / uncovered / partial)</h2><div class="source-code">${covLines}</div></div>
<div><h2>Official reference${official ? "" : " (missing)"}</h2><iframe id="ref" style="width:100%;height:380px;border:0"></iframe></div>
</div></body></html>`);
    // the official stylesheet goes into an iframe so its global .hljs rules cannot touch the Labcoat columns
    await page.evaluate(
      ([css, code, scheme]) => {
        const f = document.getElementById("ref");
        f.srcdoc = `<!doctype html><meta charset="utf-8"><style>${css}\nhtml{background:transparent;color-scheme:${scheme}}body{margin:0;background:transparent}pre{margin:0}code.hljs{display:block;padding:8px 12px;font:12px/20px ui-monospace,Menlo,Consolas,monospace;border-radius:8px}</style><pre><code class="hljs">${code}</code></pre>`;
      },
      [official, html, mode],
    );
    await page.waitForTimeout(200);
    await page.screenshot({ path: `${out}/${family}-${mode}.png`, fullPage: true });
  }
await browser.close();
console.log(`→ ${out}`);
