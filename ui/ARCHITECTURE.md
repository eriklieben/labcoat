# Labcoat UI architecture (web components)

The UI is plain TypeScript + native Custom Elements. No React, no VDOM, no template library. Reactivity is
[alien-signals](https://github.com/stackblitz/alien-signals)' graph algorithm with our own scheduler, behind a small typed
wrapper; DOM is built once with our
own `h()` helper and updated by fine-grained bindings. The UI was ported from an earlier React version; the visual
contract is the class names in the stylesheets, so components keep the DOM structure and classes they had there.

## Layout

```
ui/                                 npm workspace root (Vite app; `pnpm run build` → ../src/Labcoat/wwwroot)
  design-system/                    @eriklieben/labcoat-design-system (private, TS source, no build step)
    src/core/                       scheduler.ts, reactive.ts, h.ts, element.ts ← the foundation
    src/tokens/                     tokens.css (fonts, type scale, sizes, radii, z, motion, derived tokens), base.css, motion.css
    src/themes/                     <family>.css × 7, families.ts            ← assign semantic --lc-* colors only; THEME_FAMILIES list
    src/components/                 icon, status-icon, button, chip, badge  ← reusable primitives (+ CSS)
    README.md                       primitives, theming, full token reference
  src/
    main.ts                         creates the Store + transport, mounts <lc-app>
    app.ts                          <lc-app>: shell (header, rail, message bars, toolbar, summary, progress, workspace)
    components/<name>.ts            app components, one element per file (tree-pane.ts, settings-page.ts, todo.ts)
    store.ts transport.ts contract.ts tree.ts util.ts settings.ts theme.ts ui-defaults.ts diff.ts confetti.ts mock/
    styles/base.css shell.css shared.css   app-wide CSS, imported by main.ts right after the design system (in that order):
                                    base = helpers (paths, trace links) [@layer app.base], shell = app.ts layout (header,
                                    rail, toolbar, workspace, responsive) [@layer app.shell], shared = cross-component
                                    patterns + overrides of DS primitives [@layer app.components]
    components/<name>.css           a component's CSS, next to it and imported by its .ts, wrapped in @layer app.components;
                                    --lc-* tokens only, no [data-theme] selectors (see "CSS cascade layers" below)
  scripts/                          checks run with `pnpm run check:*` (see CONTRIBUTING.md); lib/dev-server.mjs starts Vite for them
  bench/                            Vitest micro-benchmarks (`pnpm run bench`, vitest.bench.config.ts; not part of `pnpm test`)
```

Imports: app code imports from `@eriklieben/labcoat-design-system` (or `/core`, `/components`). Never import
`alien-signals` directly.

**Generic → design system, Labcoat-specific → app.** If a piece of UI has no knowledge of tests/runs/coverage
(a button, chip, segmented control, split pane, menu), it belongs in `design-system/src/components/` with its CSS
there. Available: `icon`, `statusIcon`, `button`, `iconButton`, `chip`, `badge`, `statusBadge`, `brandLogo`,
`switchControl`, `segmented`, `tabs`/`tabPanel`/`tabPill`, `menu`/`menuButton`, `popover`, `tooltip`, `messageBar`,
`meter`/`progressBar`, `codeBlock`, `emptyState`, `propertyGrid`, `splitPane`/`splitter`, `dialog`, `kbd`, `copyButton`,
`collapsible`/`disclosure`, `skeleton`, `toast` and the motion helpers (`ui/MOTION.md`) (see the table at
the top of `design-system/README.md` and the gallery: `pnpm run gallery -w design-system`, port 5200).

## Reactivity (`design-system/src/core/reactive.ts`)

```ts
const count = signal(0);          count()  count.set(1)  count.update(n => n + 1)  count.peek()  // peek = untracked
const double = computed(() => count() * 2);   // lazy, cached, notifies only when the value changes (!==)
effect(() => console.log(count()));            // runs now + on change; returns a disposer
batch(() => { a.set(1); b.set(2); });          // effects run once at the end   (nested batches: at the end of the outermost)
untracked(() => count());                      // read without subscribing
onCleanup(() => clearInterval(id));            // runs when the current owner is disposed / the effect re-runs
```

**Scheduling.** Two layers:
- *Dependency graph* (`alien-signals/system`, the npm package, unmodified): linking reads to readers, propagating
  "dirty / maybe dirty" marks, and the lazy `checkDirty` walk that keeps computeds glitch-free. Updated by Renovate
  in its own PR (label `needs-review`, never grouped or automerged); the canary test below must pass.
- *Scheduler* (`core/scheduler.ts`, ours): signal/computed/effect nodes on top of that graph, batching and the effect
  queue. Its node operations are adapted from alien-signals' reference implementation (MIT, see
  `core/LICENSE-alien-signals.txt` and THIRD-PARTY-NOTICES.md); the queueing and flushing are ours.

Why not alien-signals' own scheduler (its main export): its `flush()` is re-entrant. When a running effect flushes
(`batch()` ends inside it, as `list()` does, or it writes a watched signal), effects run *inside* that effect's body;
if one of them changes something the running effect already read, the running effect is marked but never queued
(the graph never queues an effect mid-run) and the update is lost. Upstream also does not re-run an effect that
invalidated itself. The canary test in `test/design-system/core/scheduler.test.ts` reproduces the lost update against
upstream's scheduler, so an alien-signals update that changes this shows up as a failing test (then reconsider whether
we still need our own scheduler). Our scheduler's rules:
- A write outside any effect (event handler, timer, store publish) runs the affected effects synchronously before `set()`
  returns; inside `batch()` they run once when the outermost batch ends.
- A write made *while an effect runs* (directly, through `batch()`, or by building a `list()`) never flushes from inside that
  effect: it is queued and the outermost flush runs it after the current effect finished. So an effect never sees other
  effects run halfway through its own body, and no update is lost.
- An effect whose dependency changed during its own run (it wrote something it had already read) is re-queued and re-runs
  until stable; more than 100 runs in one flush is reported with `console.error` and stopped (a self-invalidating effect).
- Computeds stay lazy and glitch-free. Regression tests: `pnpm run check:reactive` (design-system/test/).

**Ownership.** Every effect registers with the current *owner*; disposing an owner disposes everything created
under it. Each effect run gets a fresh child owner, so whatever a run created (child effects, regions, list rows,
`onCleanup`s) is disposed before the next run. Elements create a root owner on connect and dispose it on
disconnect. Result: nothing leaks as long as you create effects/listeners **inside `render()` or inside an effect**.

Rules:
- Every `window`/`document` listener, timer, observer created in a component must be paired with `onCleanup`.
- Don't create effects in event handlers or `setTimeout` callbacks (there is no owner there → never disposed).
  Set a signal instead and let an effect created in `render()` react to it.
- Effects that write signals are fine; prefer computeds for derived values.
- Debug counters: `window.__labcoat.stats()` → `{ effects, owners }` (live). Use it to prove no leaks.

## State (`src/store.ts`)

The store keeps the exact React-era state shape and event logic (`apply(ServerEvent)` updates `this.state`
immutably). Instead of `useSyncExternalStore`, the state is published to signals **once per animation frame**:

```ts
const store = getStore();
store.$.tests()          // one field as a cached computed: dependents re-run only when that field changes
store.state$()           // whole snapshot (rarely needed)
store.get()              // latest state, synchronous, untracked (event handlers, timers)
store.send(cmd); store.viewRun(id); store.selectCoverage(t) …   // actions unchanged
```

Settings and theme are global signals: `settings()` / `setSetting(k, v)` (`src/settings.ts`),
`themePref()`, `themeFamily()`, `setThemePref`, `setThemeFamily` (`src/theme.ts`). The theme families (Catppuccin,
Aspire, GitHub, One, Dracula, Solarized, Gruvbox) come from the design system's `THEME_FAMILIES`
(`design-system/src/themes/families.ts`, re-exported by `src/theme.ts`): Settings (main pair + "More themes"), the
command palette and the gallery derive their lists from it; `index.html`'s pre-paint script has the same ids as a
whitelist (unknown → catppuccin).

`settings()`, `themePref()` and `themeFamily()` are computed: locked ? host default : (this browser's choice ?? host
default ?? built-in). The host's defaults (settings files, Aspire AppHost, env, CLI; contract Round 5) live in
`src/ui-defaults.ts` (`uiDefaults()`), read first from `<html data-labcoat-defaults>` (injected into `index.html` by
the host, also used by the pre-paint script) and then from every `state` snapshot. `setSetting` / `setThemePref` /
`setThemeFamily` do nothing for a locked setting; `resetLocalSettings()` forgets this browser's choices.

## Building DOM: `h()` (`design-system/src/core/h.ts`)

```ts
h("button", {
  class: () => cx("btn", busy() && "busy"),    // class: string | array | {name: bool|() => bool} | () => any of those
  style: { width: 24, "--tree-w": () => `${w()}px` },   // numbers get px (except unitless); --custom ok
  disabled: () => !ready(),                    // any function value = reactive binding (effect, writes only on change)
  title: "Run",                                // static
  "aria-pressed": () => on(),                  // aria-*/data-*/role → attributes (aria booleans → "true"/"false")
  onclick: (e) => run(),                       // on + lowercase → addEventListener
  ref: (el) => (this.btn = el),                // element after props + children
}, icon("play"), " Run", () => count())        // children: string | number | Node | array | null/false | () => Child
```

- A function child is a **region** (between comment markers). When a signal it read changes it is rebuilt; a
  string/number result just updates its text node. Keep regions small (leaf text) or use:
- `show(when, (value) => Child, fallback?)` — rebuilt only when truthiness flips; `value` is an accessor.
- `keyed(() => key, (key) => Child)` — rebuilt only when the key changes (`!==`). Use for "mode" switches and for
  structural parts whose shape depends on a few fields (compute a signature string, see `metaSignature` in
  tree-pane.ts).
- `list(items, item => key, (item, index) => Child)` — keyed reconciliation: rows are created once per key and
  reused; a new item object for the same key is pushed into the row's `item` signal (`item()` tracked,
  `item.peek()` in handlers); removed rows are disposed; moves are minimal. The row render should return one element.
- Render functions of `show`/`keyed`/`list` run **untracked**: reading a signal there reads it once. Put reactive
  reads into bindings (`() => …`).
- Function values are always treated as reactive bindings, except `on…` keys. To pass a function as data to an
  element, make it a callback prop named `on` + Uppercase (`onSelect`) — h() assigns those as-is.

## Elements (`design-system/src/core/element.ts`)

```ts
export interface FooProps { items: Item[]; selected: string | undefined; onPick: (id: string) => void }
export interface Foo extends FooProps {}                 // declaration merge: typed el.items / h("lc-foo", { items })
export class Foo extends LabcoatElement<FooProps> {
  static defaults: FooProps = { items: [], selected: undefined, onPick: () => {} };   // also the prop list
  static hostClass = "foo-pane";                         // the host IS the React root element (no wrapper div)
  #filter = signal("");                                  // local state: signals in (#)private fields
  protected render(): Child {                            // once per connection, in a root owner, untracked
    const visible = computed(() => this.props.items().filter((i) => i.name.includes(this.#filter())));
    return h("div", { class: "foo-list" },
      list(visible, (i) => i.id, (i) => h("button", {
        class: () => cx("foo-item", this.props.selected() === i().id && "selected"),
        onclick: () => this.onPick(i.peek().id),          // callback props: call through the accessor
      }, () => i().name)));
  }
}
define("lc-foo", Foo);
declare global { interface HTMLElementTagNameMap { "lc-foo": Foo } }
```

- Tags are `lc-*`; one element per file in `src/components/<kebab-name>.ts`; register with `define()`.
- **No Shadow DOM.** The host element replaces the React component's root element: give it the root's classes
  via `static hostClass` and its attributes (`role`, `aria-label`) in `render()`. A custom element is
  `display: inline` unless a class sets `display` — if the old root was a plain `div` without a display rule,
  set `this.style.display = "block"` (or `contents` for pure wrappers). Watch `>` selectors in the component CSS.
- Props: `this.props.x()` (tracked) inside bindings; parents pass values or accessors:
  `h("lc-foo", { items: () => store.$.tests(), onPick: (id) => … })`.
- Member names must not collide with `HTMLElement` members (`scrollTop`, `title`, `hidden`, `id`, `toggle…`):
  TypeScript reports it; use `#private` fields or another name.
- Moves (e.g. list reorder) keep the content; removal disposes it after a microtask; re-insertion renders again
  (local signal fields survive, like React state would not — reset them in `render()` if that matters).

**Events up.** Parent → child data: props. Child → parent: callback props (`onSelect`, `onRun`) — typed, cheap,
same shape as the React props. For app-wide requests between unrelated parts, use a typed window CustomEvent helper
like `requestStatusFilter()` / `onStatusFilterRequest()` in `tree.ts` (register the listener with `onCleanup`).

## Porting a React component, step by step

1. Create `src/components/<name>.ts` with `interface <Name>Props` = the React props (callbacks keep `on…` names).
2. `static defaults`, `static hostClass` = the root element's className; attributes of the root → `render()`.
3. `useState` → `signal` field; `useMemo` → `computed`; `useEffect` with deps → `effect` reading those signals
   (wrap the body in `untracked` if it should only depend on some of them); cleanup → `onCleanup`;
   `useRef` to DOM → `ref:`; `useRef` for values → plain fields/closure variables.
4. JSX → `h()`, keeping element order, classes, text (including JSX's leading spaces like `" Run all"`),
   `title`/`aria-*`. `{cond && <X/>}` → `show(...)`; `.map` with keys → `list(...)`; static `.map` → plain array.
5. Replace the slot in `app.ts` (`todo("…")`) with `h("lc-<name>", { …props as accessors… })`. Each slot has a
   comment listing the props the React version received.
6. Verify: `pnpm exec tsc --noEmit`, `pnpm run build`, `pnpm run check:leaks` (counts must stay flat), `pnpm run check:perf`
   (no long tasks).

Before (React):

```tsx
function RunBadge({ run, onOpen }: { run?: RunSummary; onOpen: (id: string) => void }) {
  const [open, setOpen] = useState(false);
  useEffect(() => { if (!run) setOpen(false); }, [run]);
  if (!run) return null;
  return (
    <button className={cx("run-badge", open && "open")} onClick={() => { setOpen(!open); onOpen(run.runId); }}>
      <Icon name="history" /> {formatRunTime(run.startedAt)}
      {run.failed > 0 && <span className="cnt-failed">{run.failed}</span>}
    </button>
  );
}
```

After:

```ts
export interface RunBadgeProps { run: RunSummary | undefined; onOpen: (id: string) => void }
export interface RunBadge extends RunBadgeProps {}
export class RunBadge extends LabcoatElement<RunBadgeProps> {
  static defaults: RunBadgeProps = { run: undefined, onOpen: () => {} };
  #open = signal(false);
  protected render() {
    this.style.display = "contents";                       // pure wrapper: the <button> is the visual root
    effect(() => { if (!this.props.run()) this.#open.set(false); });
    return show(this.props.run, (run) =>
      h("button", {
        class: () => cx("run-badge", this.#open() && "open"),
        onclick: () => { this.#open.update((v) => !v); this.onOpen(run().runId); },
      },
        icon("history"), " ", () => formatRunTime(run().startedAt),
        show(() => run().failed > 0, () => h("span", { class: "cnt-failed" }, () => run().failed))));
  }
}
define("lc-run-badge", RunBadge);
declare global { interface HTMLElementTagNameMap { "lc-run-badge": RunBadge } }
```

`<lc-tree-pane>` (`src/components/tree-pane.ts`) is the full reference: local state, persisted prefs, window
listeners, ResizeObserver, virtualization, keyed rows with per-row bindings, keyboard navigation.

## Focus, names and shared tree state (review 2)

- **Focus never drops to `<body>`.** `keepFocus(root, fallback)` (`src/util.ts`) runs in the shell and the detail pane: a
  focused control that a keyed region re-renders, removes or disables gets focus back on its equivalent (same tag, class,
  label) or the fallback. Run actions use *soft* disabling (`aria-disabled` + click guard, `soft()` in detail-pane.ts)
  so they stay focusable during the run they started; Run all ⇄ Stop hand the focus over. Palette navigation, F8 and the
  code-view test links move focus to the tree (`focusTree()`) or the page heading.
- **Accessible names come from content.** Rows, the Output rail item, Run failed and the output strip put extra
  information (outcome, counts, error count) in `.visually-hidden` spans after the visible text and hide decorative
  parts (`aria-hidden`); no `aria-label` that differs from the visible text (axe `label-content-name-mismatch`).
- **Tree state other parts use** is module-level in `tree-pane.ts`: `statusFilter`, `filterText`, `treeSort`,
  `treeGroup`, `multiSelection`, `treeFiltered`; helpers `focusTree()`, `prepareTreeLanding(key)` (reveal + locator ring
  for a test picked outside the tree), `nodeLink(key)`. The shell regroups the roots (`regroup()` in tree.ts) and runs
  "N matching" from `treeFilterPass(roots).tests`; search tokens are parsed / compiled by `parseQuery()` / `compileQuery()`
  (tree.ts).
- **Lean mode** (Round 4): the WebSocket connects with `?lean=1`; the store fetches history (`/api/tests/history`, on
  connect and per project when `historyStamps` changes; runs append to it through `testResults`, Round 7) and the
  selected test's output (`store.ensureDetails(t)`) and merges them into the tests. A big history response is merged in
  steps of `Store.historyChunk` tests (one macrotask each), so it never becomes one long task.
- **Wire vs store shapes** (Round 7, `src/test-results.ts`): result-only changes arrive as `testResults` patches
  (`applyResultPatch`); lean snapshots carry `fileIndex` into `state.files` (`normalizeTest` resolves `filePath`).
  Components only ever see full `TestNode`s with an absolute `filePath`.

## Performance rules (2000-test suites)

- Store updates arrive at most once per frame; don't add per-event work. Derive with `computed` close to use.
- Virtualize anything that can have hundreds of rows (pattern: `rows` computed → `start`/`end` computeds from
  scroll position so the window recomputes once per *row* scrolled → `list(visible, key, renderRow)`, rows
  absolutely positioned at `row.index * ROW_H`). The test tree goes one step further (`renderRows` in tree-pane.ts):
  rows stay keyed by node key, but the element of a row that leaves the window is handed to a row that enters it in the
  same update and patched instead of disposed and rebuilt. Its row render derives *everything* (key, kind, id) from the
  row signal, and per-node state (flash, status pop, tooltip) resets when the key changes. Give a virtual scroller an
  opaque background (`.tree-scroller`): Chrome then scrolls it on the compositor; a transparent one repaints the page
  every scroll frame.
- Filtering: one pass per keystroke (`filterPass` in tree.ts: row visibility, chip counts and matching tests together),
  shared by the tree pane and the shell through a small cache (`createFilterCache`, `treeFilterPass`); a query that
  implies a cached one (typing on, another status chip with the same text) only re-tests that pass's matches.
- In rows: bind leaves (`class`, `title`, text) instead of rebuilding; structural sub-parts behind `keyed()` with a
  cheap signature; use factory primitives (`icon()`, `statusIcon()`, `chip()`), not custom elements, inside rows.
- Never read a large collection inside a per-row binding; precompute per-node values in the tree build.
- Event handlers read with `.peek()` / `store.get()`; they don't need tracking.
- Measured (phase 1, ?mock=large, expand-all 2280 rows): no long tasks (> 50 ms) while scrolling, filtering,
  collapsing or during a full 2000-test run; rAF frame p95 ≈ 19 ms while scrolling 240 px/frame; ~31 rows in DOM;
  effect/owner counts flat across 5 rounds of expand/scroll/filter/collapse/page switches.
- **At 20,000 tests** (`?mock=huge`, `pnpm run check:perf:huge`, budgets in `ui/perf-budgets.json`): anything that is
  O(tests) per published frame shows up. Keep it out of the per-frame path or throttle it (the run overview snapshots
  at 2 Hz while a run is live). Iterate the tests `Map` instead of copying it into an array; build a `testKey()` string
  only when needed (e.g. only when something is quarantined). The incremental tree builder (`createTreeBuilder` in
  tree.ts) patches changed leaves; a project status change only replaces the project row (no full rebuild per
  `projectStatus` event); a run start / finish (every test's pending flag flips, no delta) is one copy-on-write walk
  (`patchAll`: new leaves and ancestors, ~35 ms at 20k in Node vs ~180 ms for a full build). Leaf `counts` are shared
  frozen objects (never mutate them); a leaf's `tkey` is its testKey string (reuse it for map lookups).
- Soak: `pnpm run check:soak` (watch-run session on the mock): heap, DOM nodes, listeners and effect/owner counts must
  stay flat after warm-up.
- Micro-benchmarks: `pnpm run bench` (Vitest bench + happy-dom, `bench/*.bench.ts`, `vitest.bench.config.ts`) times the
  hot paths in isolation: reactive propagation / batch / flush, `h()` and `list()` reconciliation, tree build / patch /
  flatten, the filter pass, `Store.apply`, diffs and the live console's ANSI / buffer code. Use it for before/after
  comparisons of one change (`pnpm run bench:ci` + `pnpm run bench:compare base.json bench-results/ui-bench.json`, see
  CONTRIBUTING.md "Benchmarks → UI (Vitest bench)"); DOM-heavy numbers are happy-dom's, not the browser's.

## Testing

- `pnpm exec tsc --noEmit` (strict) and `pnpm run build` must be clean.
- Unit tests: `pnpm test` (Vitest + happy-dom, `vitest.config.ts`). Tests live in `ui/test/`: `test/design-system/**` and
  `test/app/**` mirror the source trees; `test/setup.ts` stubs what happy-dom lacks (WAAPI `animate`, `matchMedia`,
  `startViewTransition`, observers, canvas, `WebSocket`, clipboard; `fetch` rejects unless a test stubs it) and
  `test/helpers/` has the controls (`setMedia`, `finishAnimations`, `triggerResize`, `FakeWebSocket`) and app fixtures
  (`makeStore()` with a `FakeTransport`, `flushFrame()` for the once-per-frame publish). `pnpm run test:coverage` fails
  under 85 % lines / statements / functions (overall and per package). The Playwright checks below are the e2e layer.
- Dev: `pnpm run dev` (port 5174) or `pnpm exec vite --port <n> --strictPort`, open `/?mock`, `/?mock=large`,
  `/?mock&coverage`.
- Real backend: `LABCOAT_HISTORY_DIR=$(mktemp -d) LABCOAT_ALLOWED_ORIGINS=http://localhost:5174 dotnet run --project
  src/Labcoat -- samples/Samples.slnx --no-open --port 4370`, then `MSTEST_EXPLORER_URL=http://localhost:4370 npm run
  dev` (the dev server proxies /api and /ws; the allowed origin lets the proxied page send commands).
- Checks: `pnpm run check:reactive`, `check:a11y`, `check:keyboard`, `check:leaks` (effect/owner counts must stay
  flat), `check:perf` (no long tasks). Each starts its own dev server.
- CSS refactors: `pnpm run check:styles` (below).
- Themes: `pnpm run check:themes` (WCAG contrast of every family × mode); look at the families in light and
  dark (`localStorage["mste.themeFamily"] = "dracula"`, or `node scripts/themes-app-shots.mjs <url> <out>`); the
  screenshot / axe scripts loop over every family (`LC_FAMILIES=a,b` limits). Component CSS uses `--lc-*` tokens
  only; status colors as text use `--lc-status-*-text` (see `design-system/README.md`).

## CSS cascade layers (`@layer`)

Status: **adopted** (2026-10, with the styles.css cleanup). All design-system and app CSS lives in named cascade layers:

```
@layer lc.reset, lc.tokens, lc.themes, lc.components, lc.motion, app.base, app.shell, app.components, app.utilities;
```

| Layer | What | Where |
|---|---|---|
| `lc.reset` | reset + base element styles (body, links, focus ring, kbd, scrollbars, `.icon`) | `design-system/src/tokens/base.css` |
| `lc.tokens` | fonts, non-color tokens, derived mode-aware tokens | `tokens/tokens.css` |
| `lc.themes` | the theme families (assign the semantic `--lc-*` colors) | `themes/*.css` |
| `lc.components` | design-system primitives | `components/*.css` |
| `lc.motion` | motion utilities and the motion policy (reduced / off / busy) | `tokens/motion.css` |
| `app.base` | app-wide helpers | `src/styles/base.css` |
| `app.shell` | the app shell (`app.ts`) | `src/styles/shell.css` |
| `app.components` | cross-component patterns, DS overrides, every component's CSS | `src/styles/shared.css`, `src/components/*.css` |
| `app.utilities` | state utilities applied from JS (theme-switch fade) | `src/theme.css` |

Rules:
- **Between layers the layer order wins, not specificity.** An app rule beats any design-system rule; write the selector
  the element needs, not a more specific one to win. Within a layer the usual specificity + source order applies.
- **Every app CSS file is wrapped** in its layer: `@layer app.components { … }` (header comment above it). An unlayered
  file would beat everything (and silently change the cascade), so new files must be wrapped too.
- **The order is declared first in the document**: in `index.html` (an inline `<style>`, before any stylesheet: a lazily
  loaded CSS chunk can be linked before the main bundle, and the first appearance of a layer name fixes its position;
  the minifier also drops redundant layer statements) and again at the top of `design-system/src/styles.css`, which
  imports each design-system file with `@import "…" layer(…)`. The gallery's `index.html` declares it the same way.
- **`!important` reverses the layer order**: an important declaration in an earlier layer wins. Used only where it means
  "cannot be overridden": the motion policy (`lc.motion`: reduced / off / busy) and the theme-switch fade (`app.utilities`).
- **Overrides by a host or user go in unlayered CSS** (or a layer declared after `app.utilities`): they win over all of
  Labcoat's CSS without specificity fights. Inline `style` attributes (from `h()` style props) are unaffected.
- Adopting the layers changed no computed style (0 pixel / 0 computed-style differences in all 7 families × light / dark ×
  1440 / 1000 / 620 px, motion off / reduced / system, the gallery and the production build). Three rules were adjusted
  first so their outcome does not depend on specificity across layers: the shared `.segment:hover` excludes `.on`
  segments, the pressed quarantine button keeps the toggle's hover, and the Run all / Stop crossfade (`.run-slot > .btn`)
  moved next to the shared `.btn` timing it replaces.

## Style regressions (`pnpm run check:styles`)

For CSS refactors and reviews, not every commit (it takes about 2 minutes and is not part of `pnpm run check`): **freeze
a baseline before the change, then compare.**

```
pnpm run check:styles --update    # before: writes ui/.styles-baseline/baseline (gitignored, local only)
# ... change the CSS ...
pnpm run check:styles                # after: prints the differences, exit 1 when anything changed
```

`scripts/check-styles.mjs` takes the app (`?mock&coverage&snapshots`) in six screens (main, selected snapshot test,
watch, coverage, settings, command palette) and the design-system gallery (typography, message bar, empty state,
property grid, code block, button, chip sections) in Catppuccin, GitHub and Dracula × light / dark at 1440 x 900, motion
off, with the docs screenshots' fixed clock and seeded `Math.random` (times and measured durations are zeroed in the
page). Per screen and theme it records a screenshot and the computed style of key elements (font, colors, spacing,
radius, transitions); per page kind it records the declared CSS rules (custom properties, font-size / line-height,
transition / animation by layer and selector), which is where easing and duration changes show up while motion is
off. The compare prints, per screen, per element, `property before → after` (with the themes it changed in when not
all), the changed rules, and the pixel-diff % per screenshot (threshold `--threshold=0.05`; red-on-grey diff images in
`ui/.styles-baseline/diff/`). A change you intended still exits 1: read the diff, then take a new baseline.
`--screens=selected,gallery` and `--themes=github-dark` limit a run (a partial `--update` merges into the baseline); a
URL or port argument uses a running dev server instead of starting a frozen one (no HMR, so edits made during the run
don't reload its pages).
