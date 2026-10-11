# @eriklieben/labcoat-design-system

## Available primitives

All exported from `@eriklieben/labcoat-design-system` (and `/components`). Factories return plain DOM (cheap, use in
rows); elements are self-contained (`lc-*`). Gallery with live demos: `npm run gallery -w design-system` → http://localhost:5200/.

| Primitive | Import | Factory / element | Markup classes | Status |
|---|---|---|---|---|
| Icon | `icon`, `iconSvg`, `iconNames` | `icon(name, { size })` / `<lc-icon>` | `.icon` (`labcoat` auto-uses `labcoatSmall` ≤ 20 px) | ready |
| Brand logo (color) | `brandLogo` | `brandLogo({ size, label? })` / `<lc-logo size>` | `.lc-logo > svg` (fixed brand purples; `-16` variant ≤ 20 px) | ready |
| Status icon | `statusIcon` | `statusIcon(status)` / `<lc-status-icon>` | `.st`, `.spinner` | ready |
| Button | `button`, `iconButton` | `button({ variant, icon }, …)` / `<lc-button>` | `.btn`, `.icon-btn` | ready |
| Chip | `chip` | `chip({ id, label, count, active })` / `<lc-chip>` | `.chip` | ready |
| Badge | `badge`, `statusBadge` | `badge()` / `<lc-badge>` | `.badge` | ready |
| Switch | `switchControl` | `switchControl({ checked, onChange, label })` / `<lc-switch>` | `.switch` (React Settings markup) | ready |
| Segmented control | `segmented` | `segmented({ options, value, onChange, label })` / `<lc-segmented>` | `.segmented > .segment.on` | ready |
| Tabs | `tabs`, `tabPanel`, `tabIds`, `tabPill`, `rateTone` | `tabs({ tabs, selected, onSelect, label })` / `<lc-tabs>` | `.pane-tabs > .pane-tab[aria-selected]`, `.tab-pill.tone-*` | ready |
| Popover | `popover`, `placePopup` | `popover({ open, anchor, onClose, content })` | `.lc-popover` (fixed, in body) | ready |
| Menu | `menu`, `menuButton` | `menuButton({ label, items })`, `menu({ open, anchor, items })` | `.lc-menu .lc-menu-item` | ready |
| Kbd | `kbd`, `keyLabel`, `isMac` | `kbd("Mod+K")` / `<lc-kbd keys>` | `.kbd-combo > kbd` | ready |
| Tooltip | `tooltip` | `tooltip(target, text, { placement, delay })` (returns target) | `.lc-tooltip[role=tooltip]` | ready |
| Message bar | `messageBar`, `messageBarAction` | `messageBar({ intent, title, body, actions, onDismiss, top })` / `<lc-message-bar>` | `.messagebar.mb-info/success/warning/error/snapshot[.top]` | ready |
| Meter / progress | `meter`, `progressBar`, `formatPercent`, `rateTone` | `meter({ value, label, size, showValue })`, `progressBar({ value?, label })` | `.meter.tone-* > .meter-fill`, `.lc-progress` | ready |
| Code block | `codeBlock`, `escapeHtml`, `splitHtmlLines`, type `Highlighter` | `codeBlock({ code, label, highlight?, lineNumbers, startLine, markLines, wrap })` | `.lc-code > pre > code.hljs > .lc-code-line` | ready |
| Empty state | `emptyState` | `emptyState({ icon \| spinner, title, body, actions, size })` | `.empty-state > .empty-illustration/.empty-title/.empty-sub/.empty-actions` | ready |
| Property grid | `propertyGrid` | `propertyGrid([{ name, value, badges? }], { label })` | `dl.prop-grid > dt.prop-name + dd.prop-value` | ready |
| Split pane / splitter | `splitPane`, `splitter` | `splitPane({ direction, first, second, initialSize, min, onResizeEnd, label })`, `splitter({ orientation, size, onResize, min, max, invert, onResizeEnd, label })` | `.lc-split`, `.splitter-v/-h[role=separator]` | ready |
| Dialog | `dialog` | `dialog({ open, onClose, title \| label, content, footer, size, placement: "top", bare, initialFocus })` | native `dialog.lc-dialog` (showModal) | ready |
| Copy button | `copyButton` | `copyButton({ text, label })` | `button.icon-btn.small.lc-copy[.done]` (icon morph + polite "Copied") | ready |
| Collapsible / disclosure | `collapsible`, `disclosure`, `caret` | `collapsible({ open, content })`, `disclosure({ summary, content })` | `.lc-collapsible[.open] > .lc-collapsible-inner`, `.lc-caret` | ready |
| Skeleton | `skeleton`, `delayed` | `skeleton({ lines, heading, label })`, `delayed(el, 300)` | `.lc-skeleton > .lc-skeleton-line` (appears after 300 ms) | ready |
| Toast | `toast` | `toast(message, { intent, action, duration })` | `.lc-toaster > .lc-toast` (bottom center, ≤ 2) | ready |
| Motion | `animateEnter`, `animateExit`, `presence`, `reveal`, `flip`, `tween`, `countUp`, `flash`, `locate`, `slideIndicator`, `fillIn`, `canAnimate`, `setMotionLevel`, `MOTION`, `EASE` | see `ui/MOTION.md` | `.lc-enter`, `.lc-row-enter`, `.lc-reveal`, `.lc-flash-*`, `.lc-locator`, `.lc-sheen` | ready |

Helpers: `uid`, `rovingIndex`, `portal` (with an optional `exit`), `listen`, `focusables`, `prefersReducedMotion`, `announce` (`components/util.ts`).

The Labcoat design system: a tiny reactive core, design tokens with switchable themes, and reusable `lc-*`
primitives. Plain TypeScript + native Custom Elements, no framework. Consumed as TypeScript source (the app's Vite
build compiles it; there is no separate build step).

```ts
import "@eriklieben/labcoat-design-system/styles.css"; // once, before the app's own CSS
import { h, signal, button, chip, icon } from "@eriklieben/labcoat-design-system";
```

| Export | What |
|---|---|
| `.` | everything below |
| `./core` | `signal`, `computed`, `effect`, `batch`, `untracked`, `root`, `onCleanup`, `h`, `fragment`, `list`, `show`, `keyed`, `cx`, `LabcoatElement`, `define` |
| `./components` | `icon`, `statusIcon`, `button`, `iconButton`, `chip`, `badge`, `statusBadge` + the `lc-*` elements |
| `./styles.css` | declares the cascade layers and imports tokens, themes, base, components and motion into them |

## Layout

```
src/
  core/        scheduler.ts (non-re-entrant effect scheduler on alien-signals/system), reactive.ts (typed wrapper + ownership),
               h.ts (DOM builder, list/show/keyed), element.ts
  tokens/      tokens.css (fonts, type scale, sizes, radii, stacking, motion, derived mode-aware tokens), base.css (reset, body, focus ring, scrollbars), fonts/
  themes/      one CSS file per family (aspire.css = fallback, catppuccin.css, github.css, one.css, dracula.css,
               solarized.css, gruvbox.css) — assign semantic tokens only; families.ts lists them (THEME_FAMILIES)
  components/  one .ts + .css per primitive, util.ts (uid, rovingIndex, portal, listen), icon-set.ts (Fluent UI icons used)
  assets/      labcoat(-16).svg (one-color mark), labcoat-color(-16).svg (color logo)
gallery/       index.html, main.ts, foundations.ts, components.ts, gallery.css, vite.config.ts (dev-only)
test/          reactive.test.ts + index.html (run by ui/scripts/wc/reactive-test.mjs)
```

## Primitives

Each primitive is a **factory function** that returns exactly the markup the stylesheet styles (use these in app
code: no extra host element, cheap in virtualized rows) and an **element** form for declarative use and the gallery.

| Factory | Element | Markup |
|---|---|---|
| `icon(name, { size, class, title })` | `<lc-icon name size>` | `span.icon > svg` (SVG parsed once per name, then cloned) |
| `statusIcon(status \| () => status, size)` | `<lc-status-icon status>` | `.st` icon / spinner / queued dot; the reactive form pops (`.st-anim`) only on change |
| `button({ variant, size, icon, iconClass, pressed, ...props }, ...children)` | `<lc-button variant icon label>` | `button.btn[.btn-accent\|.btn-danger\|.btn-toggle\|.btn-link][.btn-small][.on]` |
| `iconButton({ icon, label, size, toggled })` | – | `button.icon-btn` (title + aria-label) |
| `chip({ id, label, count, active, onclick })` | `<lc-chip chip-id label count>` | `button.chip.chip-<id>[.chip-active][role=radio] > .chip-dot + label + .chip-count` |
| `badge(props, ...children)`, `statusBadge(status)` | `<lc-badge [status]>` | `span.badge[.badge-status.badge-<status>]` |

The full list (switch, segmented control, tabs, menu/popover, tooltip, message bar, meter/progress, code block, empty
state, property grid, split pane, dialog, kbd, brand logo) is in **Available primitives** at the top; live demos with
variants, states, keyboard notes and usage snippets are in the gallery.

### Conventions

- **Factories first.** `switchControl()`, `segmented()`, `tabs()`, `meter()`, … return plain DOM and accept
  `MaybeReactive` props (`value` or `() => value`); callbacks are `onX`. `lc-*` elements exist where a primitive keeps its
  own state (switch, segmented, tabs, message bar, kbd, logo).
- **ARIA per WAI-ARIA APG**: switch (`role=switch`), radio group with roving tabindex (segmented), tabs with automatic
  activation, menu button + menu (arrows, Home/End, type-ahead, Esc returns focus), tooltip (`aria-describedby`, hover
  delay, Esc, hoverable), window splitter (`role=separator` + `aria-valuenow`), modal dialog (native `showModal()`).
- **Popups** (`popover`, `menu`, `tooltip`) are `position: fixed` and appended to the anchor's nearest landmark or
  dialog (else `body`): never clipped by `overflow: hidden`, inside the right accessibility subtree, usable inside modals.
- **Motion**: tokens only (`--lc-motion-*`, `--lc-ease-*`); one central policy in `tokens/motion.css`: reduced motion
  (system setting or `html[data-motion="reduced"]`) keeps short fades only, `html[data-motion="off"]` turns everything
  off, `html.lc-busy` turns decorative motion off. Rules, tokens, helpers and the app wiring: `ui/MOTION.md`.
- **Tokens only**: component CSS uses `--lc-*` tokens, so every theme family works in light and dark.

## Gallery and tests

```sh
npm run gallery -w design-system           # from ui/: http://localhost:5200/  (?theme=aspire-dark|catppuccin-light|dracula-dark|…)
node scripts/ds-gallery.mjs <outDir>       # screenshots of every section in every family × light / dark (LC_FAMILIES=a,b limits)
node scripts/themes-check.mjs              # WCAG contrast of every theme (all --lc-* pairs the UI draws); exit 1 on failures
node scripts/ds-axe.mjs                    # axe-core on the gallery (4 themes, unrolled, with menu/dialog/tooltip open)
node scripts/ds-keyboard.mjs               # keyboard checks: switch, segmented, tabs, menu, tooltip, dialog, split pane
node scripts/wc/reactive-test.mjs          # reactive core regression tests (design-system/test/)
npm test                                   # from ui/: Vitest unit tests (core, components, themes: ui/test/design-system/)
```

## Themes and tokens

Two layers:

1. **Semantic tokens** (`--lc-*`): the only colors components use. No raw colors in component CSS.
2. **Theme files** assign the semantic tokens and nothing else, one file per family, each with a light and a dark
   mode:

| Family (`data-theme-family`) | Light / dark | Palette source |
|---|---|---|
| `catppuccin` (app default) | Latte / Macchiato | Catppuccin v1.8.0 |
| `aspire` (fallback) | Light / Dark | Aspire dashboard 13.6 design tokens |
| `github` | Light Default / Dark Default | Primer Primitives v11.10.0 |
| `one` | One Light / One Dark | Atom one-light-syntax / one-dark-syntax (+ the VS Code ports for UI tones) |
| `dracula` | Alucard / Dracula | Dracula specification (draculatheme.com/spec) |
| `solarized` | Solarized Light / Solarized Dark | Ethan Schoonover's Solarized |
| `gruvbox` | Gruvbox Light / Gruvbox Dark (medium) | morhetz/gruvbox |

Licenses: `THIRD-PARTY-NOTICES.md` in the repository root. The same list as data — id, label, flavor names, modes,
swatch colors — is `THEME_FAMILIES` (`themes/families.ts`, exported from the package and from `./themes`); the
app's theme signal, Settings, command palette and this gallery all read it.

Selection is two attributes on `<html>`:

```html
<html data-theme-family="catppuccin|aspire|github|one|dracula|solarized|gruvbox" data-theme="light|dark">
```

`data-theme` is resolved from the user's mode (system / light / dark). Without `data-theme-family` (or with an
unknown one) the Aspire theme applies: `aspire.css` assigns the tokens on plain `:root`, every other family on
`:root[data-theme-family="x"]`, imported after it. `tokens/tokens.css` adds a few **derived, mode-aware** tokens built
from the semantic ones (light values on `:root`, dark on `[data-theme="dark"]`, see the reference below), so component
and app CSS never needs `[data-theme]` selectors. (The old Aspire / Fluent alias names such as `--colorNeutralBackground1`
or `--st-passed` were removed.)

### Restyling

The design system's CSS (and Labcoat's app CSS) lives in cascade layers (`lc.reset` … `lc.motion`, then `app.*`; see
`design-system/src/styles.css`). **Put your overrides in unlayered CSS** (or in a layer declared after `app.utilities`):
unlayered rules beat every layered rule regardless of specificity, so a plain `.btn { … }` wins over the design system's
`.btn-toggle.on:hover:not(:disabled)`. Only `!important` inverts this (the motion policy uses it; don't fight it).

To change colors, override the semantic tokens. Set them on `:root` (derived tokens resolve there):

```css
/* a different accent for Aspire dark */
:root[data-theme-family="aspire"][data-theme="dark"],
:root:not([data-theme-family])[data-theme="dark"] {
  --lc-accent: #ff9f1c;
  --lc-accent-hover: #ffbf69;
  --lc-on-accent: #1a1a1a;
  --lc-focus-ring: #ff9f1c;
}

/* softer page background in both Catppuccin flavors */
:root[data-theme-family="catppuccin"] {
  --lc-bg-page: var(--ctp-mantle);
}

/* Dracula: pink accent instead of purple (dark mode) */
:root[data-theme-family="dracula"][data-theme="dark"] {
  --lc-accent: var(--drac-pink);
  --lc-accent-button-bg: var(--drac-pink);
  --lc-focus-ring: var(--drac-pink);
}
```

Each family's palette variables (`--ctp-*`, `--gh-*`, `--one-*`, `--drac-*`, `--sol-*`, `--gb-*`) are available for
such overrides; re-run `scripts/themes-check.mjs` against your stylesheet's values when you change text colors.

### Adding a theme family

1. **CSS**: copy `themes/catppuccin.css` to `themes/<id>.css`. It declares the palette as variables (`--ctp-*`) and
   maps every `--lc-*` token once in `:root[data-theme-family="<id>"]` (light); `…[data-theme="dark"]` only swaps
   the palette and overrides the mappings that differ. Assign **every** token of the reference below in the light
   block: anything you leave out falls through to the Aspire values. Put the palette source, license and every
   contrast adjustment in comments. For the syntax block, take every `--lc-code-*` color (and italic / bold) from
   the family's official highlight.js theme or, without one, from its original editor theme mapped to the
   highlight.js classes; declare the syntax block again in the dark block (including `-style: normal` for anything
   the light block made italic / bold).
2. **Import** it in `styles.css` after `catppuccin.css`.
3. **List** it in `THEME_FAMILIES` (`themes/families.ts`): id, label, flavor names, `primary: false` (it then
   appears under Settings → More themes and in the command palette), and the swatches: the resolved
   `--lc-bg-page`, `--lc-fg`, `--lc-accent`, `--lc-status-passed/-failed/-skipped` per mode.
4. **Pre-paint whitelist**: add the id to the regular expression in `ui/index.html` (it runs before any module).
5. **Check**: `node scripts/themes-check.mjs` (0 hard failures; it also flags missing tokens and swatches that
   drift from the CSS), `--tune` prints the smallest `--lc-code-tint-shift` for the family; then
   `node scripts/ds-axe.mjs`, `node scripts/shell-axe.mjs <port>` and `node scripts/themes-app-shots.mjs <url> <out>`
   with `LC_FAMILIES=<id>`, and look at the screenshots.
6. **License**: add the palette's notice to `THIRD-PARTY-NOTICES.md`.

### Contrast

Theme values are checked against WCAG 2.x by `scripts/themes-check.mjs`: ≥ 4.5:1 for text (`--lc-fg*`, links,
`--lc-status-*-text`, message bars, console, ANSI and code colors on their backgrounds, text on the selected
segment), ≥ 3:1 for UI elements (status icons, focus ring, accent). Where an original palette color falls short on
our actual surfaces, the theme file moves just that token, by mixing it toward the family's text color (or toward
black / white for status text, which keeps the hue) just enough to pass; each adjustment is commented with the
original ratio. Strokes are decorative separators (Aspire's own are ~1.5:1) and are not held to 3:1.

Syntax colors come from each family's official syntax theme (in order of preference: the family's own
highlight.js theme, its original editor theme mapped to highlight.js classes, for Aspire the dashboard's
highlight.css). Where an original color is below 4.5:1 on our code background, only its OKLCH lightness moves
(hue and chroma kept), and the theme file notes the original value and ratio.

Two token pairs exist for contrast:

- `--lc-status-passed` / `-skipped` / `-failed` color icons, bars and fills (≥ 3:1); text in a status color (counts,
  coverage rates, log levels) uses `--lc-status-passed-text` / `-skipped-text` / `-failed-text` (≥ 4.5:1). The
  `.tone-good/warn/bad` helpers set both `--tone` and `--tone-text`.
- Source lines with a background tint (coverage in full-line mode, the selected test's method range, the failing
  line) keep the syntax hues: the app moves each syntax color's OKLCH lightness by the family's
  `--lc-code-tint-shift` (negative on light themes, positive on dark ones; the smallest step that keeps all of them
  ≥ 4.5:1 on every tint, see `--tune`).

### Semantic token reference

Non-color (in `tokens/tokens.css`, theme independent):

| Token | Description |
|---|---|
| `--lc-font-sans` / `--lc-font-mono` / `--lc-font-display` | UI text / code + numbers / app name |
| `--lc-font-size`, `--lc-line-height` | body text (14px / 20px) |
| `--lc-control-height`, `--lc-control-radius`, `--lc-popup-radius` | control sizing |
| `--lc-disabled-opacity` | opacity of disabled controls |
| `--lc-row-h`, `--lc-rail-w`, `--lc-scrollbar-size` | tree row height, nav rail width, scrollbar thickness |
| `--lc-motion-instant` / `-fast` / `-pop` / `-enter` / `-dialog` / `-exit` / `-base` / `-slow` / `-xslow` | durations: 80 / 120 / 140 / 160 / 180 / 100 / 200 / 320 / 600 ms (`--lc-motion-progress` = xslow) |
| `--lc-ease-standard` / `-decelerate` / `-accelerate` / `-emphasized` | Fluent 2 curves: state change / entering / leaving / moving |
| `--lc-motion-rise`, `--lc-motion-scale-in` | entrance offset (4 px) and popup scale (.97); 0 / 1 with reduced motion |
| `--lc-motion-flash` / `-pulse` / `-shimmer` / `-stripes` / `-celebrate` | one-shot flash 900 ms; loop periods 1.2 s (pulsing dots) / 1.4 s (shimmer, sweep) / 0.8 s (progress stripes); all-passed glow 1.6 s |
| `--lc-text-xs` / `-sm` / `-md` / `-base` / `-lg` / `-xl` / `-2xl` / `-3xl` / `-4xl` | type scale: 11 / 12 / 13 / 14 / 16 / 18 / 20 / 22 / 28 px (every font size uses one) |
| `--lc-radius-xs` / `-sm` / `-md` / `-lg` / `-xl` / `-pill` | 2 / 4 / 6 / 8 / 12 / 999 px (controls: `--lc-control-radius`, popups: `--lc-popup-radius`) |
| `--lc-space-2` / `-4` / `-6` / `-8` / `-10` / `-12` / `-14` / `-16` / `-20` / `-24` / `-28` / `-32` | spacing scale for padding / margin / gap (`--lc-space-N` = N px); off-scale optical values stay literal with an `/* off-scale */` comment |
| `--lc-z-sticky` / `-dropdown` / `-sheet` / `-menu` / `-popover` / `-tooltip` / `-toast` | stacking: 1 / 20 / 40 / 50 (app) and 1000 / 1100 / 1200 (design-system popups) |
| `--lc-motion-spin`, `--lc-motion-spin-reduced` | spinner turn: 0.7 s, 2.4 s with reduced motion |
| `--lc-ease-loop` | `ease-in-out`: symmetric curve for pulsing loops (the Fluent curves are one-way) |
| `--lc-dimmed-opacity` | 0.5: pending / stale / outdated / not-in-run / pruned states |

Derived, mode-aware (in `tokens/tokens.css`; light value / dark value, built from the theme's tokens):

| Token | Light / dark | Use |
|---|---|---|
| `--lc-stroke-pane` | `stroke-subtle` / `stroke-grid` | borders of panes, cards, tables and their inner separators |
| `--lc-stroke-pill` | `stroke-subtle` / `stroke` | outline of status pills (run summary, project status) |
| `--lc-bg-card` | `bg-surface` / page 50 % on surface | stat and coverage cards |
| `--lc-bg-field` | `bg-surface` / `bg-page` | search and filter fields |
| `--lc-bg-track` | `stroke-subtle` / `bg-subtle` | empty part of the run progress bar |
| `--lc-bg-row-hover` | `bg-hover` / `bg-subtle` | hovered list rows (tree, failures, coverage files) |
| `--lc-bg-control-hover` | `bg-surface` / `bg-menu-hover` | small row action buttons |
| `--lc-status-failed-count` | `status-failed-text` (both modes) | failure counts as text |
| `--lc-coverage-{covered,uncovered,partial}-line` | coverage tint 50 % on console / coverage tint | full-line coverage tint in the source view |
| `--lc-src-tint-text`, `--lc-src-tint-mix`, `--lc-src-range-mix` | `console-fg` 42 % 45 % / `fg` 35 % 35 % | line numbers and hit counts on tinted source lines |
| `--lc-src-hover-brightness` | 0.97 / 1.08 | source line hover |
| `--lc-bg-meter-track` | `bg-subtle` / `stroke` 55 % | meter and progress tracks |
| `--lc-status-failed-badge`, `--lc-status-failed-badge-bg` | failure text 75 % toward `fg` on a 12 % failure tint (both modes) | failure count badge (≥ 4.5:1 everywhere) |
| `--lc-highlight` | `#fff` (all themes) | glint / sheen on saturated fills |

Surfaces:

| Token | Description |
|---|---|
| `--lc-bg-page` | page background (workspace, panes) |
| `--lc-bg-shell` | app shell: header and nav rail |
| `--lc-bg-surface` | cards, controls, inputs, panes raised above the page |
| `--lc-bg-subtle` | subtle fills: chip counts, pressed/selected areas |
| `--lc-bg-hover` | row / list item hover |
| `--lc-bg-selected` | selected row |
| `--lc-bg-badge` | badge / property-grid pill background |
| `--lc-bg-popup` | menus and popovers |
| `--lc-bg-menu-hover` | menu item hover |
| `--lc-shadow-popup`, `--lc-shadow-menu` | popup / dropdown shadows |

Text and strokes:

| Token | Description |
|---|---|
| `--lc-fg` | primary text |
| `--lc-fg-secondary` | secondary text (chips, labels) |
| `--lc-fg-muted` | muted text: metadata, descriptions, placeholders (≥ 4.5:1) |
| `--lc-fg-subtext` | sub-labels |
| `--lc-stroke` | default borders (controls, cards) |
| `--lc-stroke-subtle` | separators inside cards |
| `--lc-stroke-hover` | control border on hover |
| `--lc-stroke-grid` | table / grid lines |

Accent and focus:

| Token | Description |
|---|---|
| `--lc-accent`, `--lc-accent-hover` | brand color: active chips/tabs/rail, toggles, selection bars |
| `--lc-on-accent` | text/icons on an accent fill |
| `--lc-accent-button-bg`, `-fg`, `-hover-bg`, `-hover-fg`, `-pressed-bg` | the accent (primary) button |
| `--lc-link`, `--lc-link-hover` | links |
| `--lc-focus-ring` | keyboard focus outline |

Status:

| Token | Description |
|---|---|
| `--lc-status-passed` / `-failed` / `-skipped` | test outcome colors (icons, progress bar, chips, counts) |
| `--lc-status-running` | spinner / in-progress |
| `--lc-status-muted` | not run / cancelled |
| `--lc-status-failed-text` | failure-colored text on tinted backgrounds (dark themes need a lighter red) |
| `--lc-status-passed-text`, `--lc-status-skipped-text` | passed / skipped colors used as text (counts, rates, warning log levels); same hue as the status color, ≥ 4.5:1 |
| `--lc-on-status` | text on a solid status fill (error badge) |
| `--lc-host-warm` | warm test host flame |
| `--lc-msg-{success,error,warning}-{bg,border}` | message bars and tinted pills |
| `--lc-msg-info-bg`, `--lc-msg-info-border`, `--lc-msg-fg` | neutral message bar |
| `--lc-msg-action-bg`, `--lc-msg-action-hover-bg` | message bar action button |

Console, code and coverage:

| Token | Description |
|---|---|
| `--lc-console-bg`, `-fg`, `-muted`, `-border`, `-hover`, `-gutter-rule` | output panel, stdout, source view |
| `--lc-console-error-bg`, `--lc-console-warn-bg` | error / warning log lines |
| `--lc-console-link` | file:line links in output |
| `--lc-ansi-{black,red,green,yellow,blue,magenta,cyan,white}` and `--lc-ansi-bright-*` | ANSI SGR colors in output |
| `--lc-code-<token>`: `default`, `comment`, `doctag`, `keyword`, `built-in`, `type`, `class` (title.class_), `function` (title.function_), `title`, `params`, `string`, `subst`, `number`, `literal`, `meta` (C# attributes, directives), `attr`, `attribute`, `variable`, `symbol`, `operator`, `punctuation`, `tag`, `name`, `addition`, `deletion`, `regexp` | syntax highlighting: one token per highlight.js class (the app's source view and the code block). Every family sets all of them from its official syntax theme (see the "syntax" blocks in each theme file) |
| `--lc-code-<token>-style`, `--lc-code-<token>-weight` | optional `italic` / `bold` for a token (default `normal`), where the official theme uses them (e.g. italic comments in One, Solarized, Gruvbox; bold keywords in Dracula) |
| `--lc-coverage-covered`, `-uncovered`, `-partial` | coverage line backgrounds |
| `--lc-code-tint-shift` | OKLCH lightness step (a number, e.g. `-0.04`) applied to syntax colors on tinted source lines |

Misc:

| Token | Description |
|---|---|
| `--lc-scrollbar-thumb`, `--lc-scrollbar-thumb-hover` | scrollbars |
| `--lc-tag-{teal,marigold,peach,coral,royal-blue,orchid,brand-blue,seafoam,jade,berry}` | per-project accent palette |
| `--lc-confetti-1` … `--lc-confetti-7` | confetti burst colors |

## License

MIT. The Aspire theme derives from the Aspire dashboard design tokens (MIT, .NET Foundation); the other families use
the Catppuccin palette (MIT, © 2021 Catppuccin), GitHub Primer Primitives (MIT, © 2018 GitHub Inc.), Atom One
syntax (MIT, © 2016 GitHub Inc.), Dracula (MIT, © 2023 Dracula Theme), Solarized (MIT, © 2011 Ethan Schoonover)
and gruvbox (MIT, © 2018 Pavel Pertsev). Icons: Fluent UI System Icons (MIT, Microsoft). See
`THIRD-PARTY-NOTICES.md` in the repository root.
