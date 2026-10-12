# Screenshot list

Every image the docs reference, and the exact state to capture, so the screenshot pass is mechanical. Images that
don't exist yet show as broken links until this list is done. Keep the file names: the README, `docs/*.md` and the
package READMEs (through `https://raw.githubusercontent.com/eriklieben/labcoat/main/docs/images/...`) point at them.

## Taking them

All images except `aspire-trace-dashboard.png` come from one script, `ui/scripts/docs-screenshots.mjs`:

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
| `random-order.png` | features | Mock `?mock`. After **Run all**, press `Ctrl+K`, type `random order`, Enter (the mock runs in random order with seed 48213). Press `Ctrl+K` again and type `random`: the palette lists "Run in random order"; behind it the run overview shows "random order, seed 48213". | Full window |
| `build-errors.png` | features | Mock `?mock` without clearing the build failure. Click **Show errors** in the red banner: the build errors list with file links. | Full window |
| `settings-themes.png` | features | Mock `?mock`, after **Run all**. **Settings** in the left rail, **More themes** expanded so all seven families show (Mode shows **Dark**, Catppuccin selected). Motion left at its default (System). | Full window |
| `aspire-dashboard.png` | aspire.md | Real Aspire (`dotnet run --project samples/AspireSample/AspireSample.AppHost`), `LC_ASPIRE_DASHBOARD=<dashboard url>`. **Resources** page, the `tests-failing` resource's **...** menu open and **Commands** hovered: Run failed tests, Cancel run, Restart test host, Toggle watch mode / code coverage / test tracing. Nothing is clicked in the menu. The "Endpoint is unsecured" bar is hidden and local paths in **Source** are shortened to `~/src/labcoat` (in the script's browser only). Dark (the dashboard default). | Dashboard, top down to below the menu |
| `aspire-failure-context.png` | aspire.md | Mock `?mock&tracing` (tracing on). After **Run all**, select `WeatherPage_RendersForecastTable` (timed out): the error box shows "Service activity during this test" (the `apiservice` / `webfrontend` 500 spans, the error and warning logs) with **Trace of this test** / **Service logs** links. | Detail pane, down to the history strip |
| `aspire-trace-dashboard.png` | aspire.md, AspireSample README | Exists (Aspire's trace view of a failing test, Aspire 13.6). Retake by hand only if the dashboard changed: real Aspire, tracing on for `tests-failing`, run `Demo_intentionally_failing_expects_wrong_status_code` in its Labcoat UI, click **View trace**. | Dashboard window |
| `themes/<family>-<mode>.png` | themes.md | 14 images: families `catppuccin`, `aspire`, `github`, `one`, `dracula`, `solarized`, `gruvbox` x modes `light`, `dark`. Mock `?mock&coverage`, same state as the hero (cleared build failure, **Run all**, `Render_FormatsReceipt` selected). Viewport 1280 x 800, device scale factor 1. | Full window |

## Checks after the pass

- Every file above exists, and nothing in `docs/images/` is unreferenced (except this list).
- Every image except `hero-light.png` and `themes/*-light.png` is dark; light and dark pairs show the same state.
- No personal paths, user names or real repository names in the shots (the script rewrites the mock's paths to `/src/demo/...`).
- Run the link check: `node eng/check-links.mjs` (reports missing images and broken links).
