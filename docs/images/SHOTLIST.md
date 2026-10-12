# Screenshot list

Every image the docs reference, and the exact state to capture, so the screenshot pass is mechanical. Images that
don't exist yet show as broken links until this list is done. Keep the file names: the README, `docs/*.md` and the
package READMEs (through `https://raw.githubusercontent.com/eriklieben/labcoat/main/docs/images/...`) point at them.

## Taking them

All images except `aspire-trace-dashboard.png` and `markdown-report.png` come from one script, `ui/scripts/docs-screenshots.mjs`:

```sh
cd ui
npm run screenshots:docs                                   # starts a Vite dev server with the current ui/src
npm run screenshots:docs -- http://127.0.0.1:5370/         # or use a running server (e.g. `npx vite preview`)
LC_SHOTS=hero-dark,themes npm run screenshots:docs         # only some images ("themes" = all theme images)
LABCOAT_CHECK_PORT=5470 npm run screenshots:docs           # the dev server's first port to try (default 5300)
LC_ASPIRE_DASHBOARD=http://localhost:15179/ npm run screenshots:docs   # also aspire-dashboard.png (AppHost running)
```

What the script does, so the images stay comparable:

- **Mock backend** (`?mock`, plus `&coverage` / `&tracing` where noted), so no .NET is needed and the data is the same
  every time: `Math.random` is seeded and the clock starts at 8 Oct 2026 10:24 (all "Last run …" times match).
- **No local paths:** the mock's sample root is rewritten to `/src/demo/...` in the served scripts (the browser's copy
  only).
- **Viewport** 1440 x 900 at device scale factor 2. Full-window images are saved at 1.5x (2160 px wide), crops at 2x.
  PNGs are re-encoded with ImageMagick; one still over 400 KB is reduced to a 256-color palette (no dithering).
- **Theme** through `localStorage` (`mste.theme`, `mste.themeFamily`) before the page loads, `colorScheme` to match.
  **Dark by default:** family `catppuccin` (the default family), dark (Macchiato), unless noted. The only light images
  are `hero-light.png` (the NuGet package READMEs) and the light half of the theme gallery.
- **Motion off** (Settings → Motion, `html[data-motion="off"]`) plus emulated `prefers-reduced-motion`; confetti off;
  the mouse is moved out of the way before each capture.
- **The mock's build failure** (`Inventory.Tests`) is cleared first (**Rediscover** twice), then **Run all** runs once,
  for every image except `build-errors.png`.
- **Glitch guard:** before each capture the visible tree rows must be evenly spaced and `CalculatorTests` must show all
  four methods; a failed check retakes the image once and is reported.

## Images

| File | Where | State to capture | Crop |
| --- | --- | --- | --- |
| `hero-dark.png` | README hero | Mock `?mock&coverage`, dark. Clear the build failure, **Run all**. Select `Render_FormatsReceipt` (SampleTests › SampleTests.Orders › ReceiptTests): the detail pane shows the multi-line expected/actual diff. Tree fully visible on the left. | Full window |
| `hero-light.png` | Tool and Aspire package READMEs (`src/Labcoat/README.md`, `src/Labcoat.Aspire.Hosting/README.md`) | Same as `hero-dark.png`, light. | Full window |
| `failure-diff.png` | README highlights, features | As the hero, but select `Serialize_ProducesExpectedJson` (JSON diff). | The detail pane only (right column), from the test title to the end of the diff |
| `failure-stack.png` | features | Mock `?mock&coverage`. Select `Cache_RoundTripsValue`, click the `CacheService.cs:27` frame in **Stack trace**: the source viewer switches to `CacheService.cs` with the error line marked. Viewport 1440 x 1500. | Detail pane, from **Source** to the end of **Stack trace** |
| `tree-filters.png` | features | After **Run all**, click the **Failed** chip and type `proj:Sample` in the filter box: flat list of the 8 failing tests, chips showing counts. | Tree pane, down to just below the last row |
| `watch.png` | features | Mock `?mock&coverage`. After **Run all**, turn on **Watch**; the mock's simulated file change (~3 s later) rebuilds and runs the affected tests. Capture after that run: the watch strip shows "CalculatorTests.cs saved → build … → 6 affected → …" (the six tests in CalculatorTests.cs: the "tests in the changed test file" strategy). | Toolbar + watch strip + top of the tree |
| `coverage-report.png` | README highlights, features | Mock `?mock&coverage`. **Run all**, open **Coverage** in the left rail, click `OrderService.cs`: the source viewer shows covered, partial and uncovered lines. | Full window |
| `coverage-line-tests.png` | features | Mock `?mock&coverage`, viewport 1440 x 1300. On the coverage page with `OrderService.cs` open, click the hit count of line 23 (`CalculateTotal`): the popover lists the tests that cover it. | Left part of the source viewer around the popover |
| `run-compare.png` | README highlights, features | Mock `?mock`. After **Run all**, open the **Live** menu in the header, **Compare with…**, pick the third run in the list: New failures, Fixed, Still failing, Newly slow. | The compare sheet |
| `command-palette.png` | README highlights, features | Mock `?mock`. Press `Ctrl+K`, type `run`: the palette lists run commands and matching tests. | Full window |
| `build-errors.png` | features | Mock `?mock` without clearing the build failure. Click **Show errors** in the red banner: the build errors list with file links. | Full window |
| `settings-themes.png` | features | Mock `?mock`, after **Run all**. **Settings** in the left rail, **More themes** expanded so all seven families show (Mode shows **Dark**, Catppuccin selected). Motion left at its default (System). | Full window |
| `export-menu.png` | features | Mock `?mock`. After **Run all**, open the **Live** menu in the header: **Compare with…**, then the HTML report / JUnit XML / Markdown / TRX / SARIF export entries, and the first runs. | The menu, down to the first three runs |
| `aspire-dashboard.png` | aspire.md | Real Aspire (`dotnet run --project samples/AspireSample/AspireSample.AppHost`), `LC_ASPIRE_DASHBOARD=<dashboard url>`. **Resources** page, the `tests-failing` resource's **...** menu open and **Commands** hovered: Run failed tests, Cancel run, Restart test host, Toggle watch mode / code coverage / test tracing. Nothing is clicked in the menu. The "Endpoint is unsecured" bar is hidden and local paths in **Source** are shortened to `~/src/labcoat` (in the script's browser only). Dark (the dashboard default). | Dashboard, top down to below the menu |
| `aspire-failure-context.png` | aspire.md | Mock `?mock&tracing` (tracing on). After **Run all**, select `WeatherPage_RendersForecastTable` (timed out): the error box shows "Service activity during this test" (the `apiservice` / `webfrontend` 500 spans, the error and warning logs) with **Trace of this test** / **Service logs** links. | Detail pane, down to the history strip |
| `aspire-trace-dashboard.png` | aspire.md, AspireSample README | Exists (Aspire's trace view of a failing test, Aspire 13.6). Retake by hand only if the dashboard changed: real Aspire, tracing on for `tests-failing`, run `Demo_intentionally_failing_expects_wrong_status_code` in its Labcoat UI, click **View trace**. | Dashboard window |
| `markdown-report.png` | standalone.md | **Manual, not from the mock** (see [markdown-report.png](#markdown-reportpng-by-hand)). The Markdown report of a fixed run (2 failed of 8, stack traces folded) rendered by GitHub's Markdown API in a light, GitHub-style page. No coverage section. | The rendered report, 880 px wide at 2x |
| `themes/<family>-<mode>.png` | themes.md | 14 images: families `catppuccin`, `aspire`, `github`, `one`, `dracula`, `solarized`, `gruvbox` x modes `light`, `dark`. Mock `?mock&coverage`, same state as the hero (cleared build failure, **Run all**, `Render_FormatsReceipt` selected). Viewport 1280 x 800, device scale factor 1. | Full window |

## markdown-report.png, by hand

GitHub renders the report differently from any local Markdown viewer, so this image is the real `MarkdownReport` output
of a small fixed run, rendered by `gh api markdown` and screenshotted in a light GitHub-style page. The fixture has no
paths or machine details (workspace `shop`, version `1.1.1`, run `run-1`, relative source files), so the image is the
same on every pass. Needs `gh` (logged in), Node with Playwright (`ui/node_modules`) and ImageMagick.

1. A throwaway test in `tests/Labcoat.Tests/Unit/` (do not commit it) that writes the report to `$TMPSHOT_OUT`; it uses the
   `ExportTests` helpers `Export`, `T` and `At`:

```csharp
namespace Labcoat.Tests.Unit;

public partial class ExportTests
{
    [TestMethod]
    public void TmpShot()
    {
        var e = Export(
            T("a1", Outcome.Passed, "Add_ReturnsSum", cls: "CalculatorTests", ns: "Shop", ms: 4),
            T("a2", Outcome.Passed, "Subtract_ReturnsDifference", cls: "CalculatorTests", ns: "Shop", ms: 3),
            T("a3", Outcome.Passed, "Checkout_AppliesDiscount", ms: 182),
            T("a4", Outcome.Passed, "Cache_RoundTripsValue", cls: "CacheTests", ns: "Shop", ms: 1420),
            T("a5", Outcome.Passed, "Import_LargeFile", cls: "ImportTests", ns: "Shop", ms: 2650),
            At(T("f1", Outcome.Failed, "Render_FormatsReceipt", cls: "ReceiptTests", ns: "Shop.Orders", ms: 36, type: "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException",
                message: "Assert.AreEqual failed. Expected:<Total: 12.50>. Actual:<Total: 12.5>.",
                stack: "at Shop.Orders.ReceiptTests.Render_FormatsReceipt() in src/Shop.Tests/Orders/ReceiptTests.cs:line 41"), "src/Shop.Tests/Orders/ReceiptTests.cs", 41),
            At(T("f2", Outcome.Failed, "Pay_RejectsExpiredCard", cls: "CartTests", ns: "Shop", ms: 58, type: "System.InvalidOperationException",
                message: "System.InvalidOperationException: The card expired on 2026-09-30.",
                stack: "at Shop.Cart.Pay(Card card) in src/Shop/Cart.cs:line 31\n   at Shop.CartTests.Pay_RejectsExpiredCard() in src/Shop.Tests/CartTests.cs:line 77"), "src/Shop/Cart.cs", 31),
            T("s1", Outcome.Skipped, "Sync_WithRemote", cls: "SyncTests", ns: "Shop", message: "Needs a network"));
        File.WriteAllText(Environment.GetEnvironmentVariable("TMPSHOT_OUT")!, MarkdownReport.ToMarkdown(e with { Version = "1.1.1", WorkspaceName = "shop" }));
    }
}
```

2. Run it, render it and take the screenshot (`shot.mjs` below, run from `ui/` so `playwright` resolves):

```sh
TMPSHOT_OUT=/tmp/report.md dotnet test tests/Labcoat.Tests --filter "Name=TmpShot"
gh api markdown -f text="$(cat /tmp/report.md)" -f mode=gfm > /tmp/body.html
cd ui && node /tmp/shot.mjs /tmp/body.html ../docs/images/markdown-report.png
magick ../docs/images/markdown-report.png -strip -define png:compression-level=9 -define png:compression-filter=5 ../docs/images/markdown-report.png
```

```js
import { chromium } from "playwright";
import { readFileSync } from "node:fs";
const [body, out] = process.argv.slice(2);
const css = `
body{margin:0;background:#fff;font:16px/1.5 -apple-system,"Segoe UI",Helvetica,Arial,sans-serif;color:#1f2328}
.markdown-body{box-sizing:border-box;width:880px;padding:32px 40px}
h2{font-size:1.5em;border-bottom:1px solid #d1d9e0;padding-bottom:.3em;margin:0 0 16px;font-weight:600}
h3{font-size:1.25em;font-weight:600;margin:24px 0 16px}
p{margin:0 0 16px}
table{border-collapse:collapse;margin:0 0 16px;display:block;width:max-content;max-width:100%}
th,td{padding:6px 13px;border:1px solid #d1d9e0}th{font-weight:600}tr:nth-child(2n){background:#f6f8fa}
pre{background:#f6f8fa;border-radius:6px;padding:16px;overflow:auto;font-size:85%;line-height:1.45;margin:0 0 16px}
code,pre{font-family:ui-monospace,SFMono-Regular,"SF Mono",Menlo,Consolas,monospace}
code{background:#818b981f;border-radius:6px;padding:.2em .4em;font-size:85%}pre code{background:none;padding:0;font-size:100%}
details{margin:0 0 16px}summary{cursor:pointer}details>pre{margin-top:16px}
sub{font-size:12px;color:#59636e}
.anchor{display:none}`;
const html = `<!doctype html><meta charset=utf-8><style>${css}</style><div class="markdown-body" id="r">${readFileSync(body, "utf8")}</div>`;
const b = await chromium.launch();
const p = await b.newPage({ viewport: { width: 880, height: 900 }, deviceScaleFactor: 2, colorScheme: "light" });
await p.setContent(html);
await p.locator("details").evaluateAll((ds) => ds.forEach((d) => (d.open = false)));
await p.locator("#r").screenshot({ path: out });
await b.close();
```

3. Delete the throwaway test.

## Checks after the pass

- Every file above exists, and nothing in `docs/images/` is unreferenced (except this list).
- Every image except `hero-light.png` and `themes/*-light.png` is dark; light and dark pairs show the same state.
- No personal paths, user names or real repository names in the shots (the script rewrites the mock's paths to `/src/demo/...`).
- Run the link check: `node eng/check-links.mjs` (reports missing images and broken links).
