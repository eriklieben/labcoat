# Labcoat motion

Micro-animations that orient (where did this come from), confirm (the action registered) or connect two states. If
an animation does none of these it doesn't ship. Spec and rationale: the motion proposals (P1–P19); live demos with a
motion switch: the design-system gallery, section **Foundations → Motion** (`npm run gallery -w design-system`).

## Rules

1. **Tokens only.** No literal durations or curves in component CSS; JS uses `MOTION` / `EASE` from the design system.
2. **transform / opacity only.** One exception: a single section's height reveal (`grid-template-rows` 0fr → 1fr:
   `reveal()`, `collapsible()`). One-shot `box-shadow` on a pseudo-element is fine (locator ring, live ripple).
3. **Never animate**: virtualized rows during scroll, numbers while a run is in flight (count-up only on run finish),
   large lists as a whole (≤ 8 staggered rows), anything in the palette list while typing, keyboard scrubbing (a
   selection that changes again within 150 ms swaps instantly), split panes / panel resizes, anything on first paint.
4. **Exits are ~30 % shorter** than entrances and use `--lc-ease-accelerate`; entrances use `--lc-ease-decelerate`;
   things that move use `--lc-ease-emphasized`; color/opacity state changes use `--lc-ease-standard`. One-shot
   feedback that fades out (`.lc-flash-*`, run-complete settle, live ripple) and the theme fade use
   `--lc-ease-decelerate` too: no bare `ease-out`.
5. **Never bouncy**: overshoot ≤ 10 % (status pop), no springs, no shakes.
6. **Respect the motion level and the busy gate** (below). The CSS policy is central (`tokens/motion.css`); JS helpers
   check `canAnimate()`.

## Tokens (`design-system/src/tokens/tokens.css`)

| Token | Value | Use |
|---|---|---|
| `--lc-motion-instant` | 80 ms | press, exit of tiny things, hover reveal (row actions), Run/Stop out |
| `--lc-motion-fast` | 120 ms | hover/press colors, crossfades, tooltip; the reduced-motion cap |
| `--lc-motion-pop` | 140 ms | popover/menu in, list row in, step reveal |
| `--lc-motion-enter` | 160 ms | content swap (`.lc-enter`), caret, switch thumb, palette in |
| `--lc-motion-dialog` | 180 ms | centered dialog in |
| `--lc-motion-exit` | 100 ms | dialog / palette out, submenu fade |
| `--lc-motion-base` | 200 ms | tab / rail indicator, height reveal, meter value change, history shift, theme fade |
| `--lc-motion-slow` | 320 ms | meter fill on first display, count-up |
| `--lc-motion-xslow` (= `--lc-motion-progress`) | 600 ms | run progress layers, locator ring, sheen, live ripple |
| `--lc-motion-flash` | 900 ms | one-shot flash: `.lc-flash-*`, run-complete brightness |
| `--lc-motion-pulse` / `-shimmer` / `-stripes` | 1.2 s / 1.4 s / 0.8 s | loop periods: pulsing dots / skeleton shimmer + indeterminate sweep / in-run progress stripes |
| `--lc-motion-celebrate` | 1.6 s | all-passed summary glow (decorative, once) |
| `--lc-ease-standard` | `cubic-bezier(.33,0,.67,1)` | Fluent easyEase: color/opacity |
| `--lc-ease-decelerate` | `cubic-bezier(.1,.9,.2,1)` | Fluent decelerateMax: entering |
| `--lc-ease-accelerate` | `cubic-bezier(.7,0,1,.5)` | Fluent accelerateMid: leaving |
| `--lc-ease-emphasized` | `cubic-bezier(.22,1,.36,1)` | ease-out-quint: moving |
| `--lc-motion-rise` | 4 px (0 reduced) | entrance offset |
| `--lc-motion-scale-in` | .97 (1 reduced) | popup entrance scale |

## Motion level and the busy gate

- **System** (default): follows `prefers-reduced-motion`.
- **Reduced** (`html[data-motion="reduced"]`, or the system setting): no movement. Rise/scale tokens become 0/1 so
  every entrance is a short fade; `base`/`slow`/`enter`/`dialog` are capped at `fast`; loops stop (pulses, shimmer,
  sheen) and spinners slow to 2.4 s; transform transitions (meters, switch thumb, caret, collapsible) are instant;
  JS movement (FLIP, tab slide, meter fill, count-up, tree slide) is skipped.
- **Off** (`html[data-motion="off"]`): no transitions or animations at all, except spinners (state).
- `html[data-motion="full"]` ignores the system setting (gallery only).
- **Busy** (`html.lc-busy`, set by the shell while a run of more than 200 tests is in flight and while the tab is
  hidden): decorative motion is off — `.lc-row-enter`, `.lc-enter-x`, `.lc-sheen`, skeleton shimmer, `.lc-decor`,
  count-up, history-strip shift, tree slide for nodes with > 60 tests. State motion stays: progress layers, spinners,
  status pop, the P6 regression/fix flash (rare by construction).

Settings → Appearance → **Motion** (System / Reduced / Off) is stored in `mste.settings.motion` and applied before first
paint by the pre-paint script in `index.html` (`setMotionLevel()` at runtime). `prefersReducedMotion()` (DS) reads both.

## Design-system utilities

CSS (`tokens/motion.css`):

| Class | What |
|---|---|
| `.lc-enter` | opacity 0→1 + 4 px rise, `--lc-motion-enter`, decelerate (content swap, page switch) |
| `.lc-enter-fade` | opacity only, `--lc-motion-exit` (submenu swap, label crossfade) |
| `.lc-row-enter` | from 4 px above, `--lc-motion-pop`, stagger `--lc-i` × 15 ms (≤ 8) |
| `.lc-enter-x` | from 4 px left, `--lc-motion-pop`, delay `--lc-delay` (steps, annotations) |
| `.lc-flash-bad/good/accent` | one 900 ms tint on `::before`, decelerate |
| `.lc-locator` | 600 ms inset accent ring on `::after` ("you landed here") |
| `.lc-sheen` | one gradient sweep on `::after` (host: position relative + overflow hidden) |
| `.lc-caret` | rotates 90° when it has `.open` or its parent has `aria-expanded="true"` |
| `.lc-reveal` / `.lc-collapsible` | grid 0fr ⇄ 1fr height reserve (columns `minmax(0, 1fr)`: never widens its pane) |
| `.lc-skeleton`, `.lc-skeleton-line`, `.lc-delayed` | appear after 300 ms (CSS delay, no timer), slow shimmer |
| `.lc-pulse` | state loop ("connecting"); stops with reduced motion |
| `.lc-tabular` | tabular numbers (count-up) |

JS (`components/motion.ts`, exported from the package):

| Helper | What |
|---|---|
| `motionLevel()`, `setMotionLevel(l)`, `isBusy()`, `canAnimate("move" \| "fade" \| "decorative")`, `dur(ms)` | policy |
| `animateEnter(el, kind?, { index, delay })` | adds the utility class, removes it on `animationend` (moves/re-inserts never replay) |
| `animateExit(el, then?, { keyframes, duration })` | WAAPI exit, inert while leaving, then `then()` (default remove); runs `then` at once when motion is off or the element is detached |
| `presence(when, render, { enter, exit, appear })` | `show()` with an exit: the leaving element is moved out of the region, animated, then removed |
| `reveal(when, render)` | presence + height reserve (P3 message bars); no motion for bars present on first render |
| `flip(els, mutate, opts)` | FLIP: rects → `mutate()` → rects → animate the delta (`translate`, composite add) |
| `tween(from, to, onFrame)`, `countUp(el, from, to, { format })` | number tweens (decorative); `countUp` only for plain text nodes, drive a signal with `tween` inside h() regions |
| `flash(el, tone)`, `locate(el)`, `replayClass(el, cls, ms)` | one-shot feedback (≤ 10 flashes at once) |
| `scrubGuard(ms)`, `after(ms, fn)`, `scrollToTop(scroller, top)` | scrubbing guard, owned timeout, smooth scroll only for ≤ 2 viewports |
| `slideIndicator(a, b, orientation, pseudo)` (tabs.ts) | FLIP of a selection indicator pseudo-element |
| `fillIn(rootOrFills, startIndex)` (meter.ts) | meters grow 0 → value once (20 ms stagger, ≤ 12) |
| `announce(text)` (util.ts) | polite live region |

Primitives with motion built in: `popover`/`menu` (scale from the anchor side via `transform-origin` from
`data-side`/`data-align`, 140 ms; 80 ms fade out), `tabs` (sliding underline), `dialog` (native enter/exit with
`@starting-style` + `overlay`/`display` allow-discrete; palette drops from the top), `meter`/`progressBar` (scaleX, no
width), `statusIcon` (pop ≤ 10 % overshoot), `switchControl` (160 ms emphasized, thumb widens on press), `segmented`
(press scale), `copyButton` (P8 icon morph + "Copied" announcement), `collapsible`/`disclosure`/`caret` (P14),
`skeleton`/`delayed` (P16), `toast` (P19), `messageBar` (dismiss exit; wrap conditional bars in `reveal()`).

## Wiring per app file

Phase A (owned app files) and phase B (former SHELL files) are both implemented. What each file does:

| File | Element | Helper / class |
|---|---|---|
| `app.ts` | `<html>` | `html.lc-busy` from `currentRun` (> 200 tests) or `document.hidden` (effect + `visibilitychange`) |
| `app.ts` | nav rail `.rail-item.active::before` | `slideIndicator(from, to, "vertical", "::before")` on `page` change (P12) |
| `app.ts` | page content (`keyed(page)`) | `pageEnter()` → `animateEnter(el)` (not on first paint) (P12) |
| `app.ts` | Test details / Coverage `.pane-tab::after` | `slideIndicator()` on `view` change (P5) |
| `app.ts` | shell message bars (connection lost, editor notice, viewing a run, build failed) | `reveal()` instead of `show()` (P3) |
| `styles/shell.css` | `.run-slot > .btn` | Run all ⇄ Stop: out 80 ms accelerate, in 120 ms decelerate after 80 ms (no overlap) (P18) |
| `tree-pane.css` | `.row-actions` / `.row-meta` | always laid out; opacity 80 ms on hover/selection, `visibility` after the fade (P18) |
| `tree-pane.css` | `.tree-row` | `top: 0` + individual `translate` (set by tree-pane); `.twisty .icon` 160 ms emphasized (P2) |
| `detail-pane.css` | `.dist-bar .seg` | full-width layers, `translateX(start%) scaleX(share)` (P10) |
| `suite-status.css` | `.rp-track` | sheen host (overflow hidden); progress layers use `--lc-motion-progress` |
| app CSS, `theme.css` | every reduced-motion rule | `:root[data-motion="reduced"] …` + `@media (prefers-reduced-motion) { :root:not([data-motion]) … }` |
| `tree-pane.ts` | toggle (click, ←/→, Space) | `flip(rows, mutate, { duration: 180 })`; new rows `animateEnter(row, "row", { index })` in a 220 ms window if the list didn't scroll (P2) |
| `tree-pane.ts` | row (test leaf) | `flash(row, "bad" \| "good")` when the settled outcome changes class passed ⇄ failed (P6) |
| `tree-pane.ts` | twisty icon | `.lc-caret` |
| `suite-status.ts` | `.summary-label` | `tween()` from the pre-run suite to the final one on run finish (P11) |
| `suite-status.ts` | `.rp-track` | `replayClass(track, "lc-sheen", 600)` when the run went red → green (P11; confetti stays opt-in in Settings) |
| `watch-strip.ts` | `.ws-step` / `.ws-arrow` | `animateEnter(el, "x")` once per step per cycle; new cycle `animateEnter(host, "fade")`; "n fixed / n new" `flash()` (P13) |
| `command-palette.ts` | `.palette-list` | `animateEnter(list, "fade")` when the submenu changes (P9) |
| `detail-pane.ts` | content root per selection | `animateEnter(el)` unless `scrubGuard(150)` says scrubbing; run overview root too (P1) |
| `detail-pane.ts` | sections | `collapsible()` + `caret()` (P14); copy buttons → DS `copyButton()` (P8) |
| `code-view.ts` | annotation box / landing line | `animateEnter(box, "x", { delay: 120 })`; `scrollToTop()` + `locate(row)` on navigation (P7) |
| `run-overview.ts` | failure rows | `animateEnter(row, "row", { index })` when ≤ 5 arrive at once (P17) |
| `history-strip.ts` | squares / spark dot | shift 12 px, new square pops, dot fades (decorative) (P17) |
| `coverage-pane.ts` | overview / source meters, summary % | `fillIn(host)` on first display, `tween()` count-up of the card value (P10) |
| `run-switcher.ts` | run menu | DS `popover()` (`bottom-end`, origin top right) (P4); live dot `.lc-pulse` while connecting, static grey offline, `replayClass(dot, "ripple")` on reconnect, label crossfade (P15) |
| `settings-page.ts`, `settings.ts`, `index.html` | Motion setting | System / Reduced / Off → `setMotionLevel()` |
| `theme.ts`, `confetti.ts` | theme fade, confetti | theme fade: view transition (or a color transition fallback), `--lc-motion-base`, decelerate (`theme.css`); both skip with `prefersReducedMotion()` (system or setting) |

## Verifying

- Gallery frame sampling, app/shell sampling, reduced/off runs: Playwright scripts that start an action inside
  `page.evaluate` and read `getComputedStyle()` at fixed offsets (mid-animation values must differ from the end
  values; with Reduced every sampled `transform` must be the identity).
- `?mock=large` + `scripts/wc/perf.mjs`: long tasks during a 2000-test run must not increase, and with `html.lc-busy`
  `document.getAnimations().length` stays < 50 (status pops of in-view rows + spinners).
