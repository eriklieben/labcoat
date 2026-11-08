# Accessibility

How accessibility is handled in Labcoat: what we aim for, what is checked, what isn't, and how to report a barrier.
This is a working agreement for users, contributors and coding agents. It is not a WCAG conformance claim, an
Accessibility Conformance Report (VPAT) or legal advice.

| | |
| --- | --- |
| **Target** | [WCAG 2.2](https://www.w3.org/TR/WCAG22/) Level AA for the web UI and the HTML report |
| **Status** | Target only: no evaluation has been done that would support a conformance claim |
| **Owner** | The maintainer ([CONTRIBUTING.md](CONTRIBUTING.md#what-goes-into-labcoat)) |
| **Report a barrier** | [Bug report](https://github.com/eriklieben/labcoat/issues/new?template=bug_report.yml), see [below](#reporting-a-barrier) |
| **Last reviewed** | 2026-11-08 |

## Scope

**In scope**

- The web UI (`ui/`) and the design system it is built on (`ui/design-system`), in every theme family, light and dark.
- The HTML report export (`src/Labcoat/Export`).
- The documentation in this repository.

**Not currently in scope**

- Terminal output of the `labcoat` command: its text is not evaluated. The results it reports are also available
  through the UI and the report exports.
- What Labcoat shows but doesn't author: your test names, test output and source code.
- Other people's tools Labcoat links to or opens: the Aspire dashboard and your editor. A barrier there belongs to
  that project, but tell us if Labcoat could work around it.

## What the UI does

- **Keyboard:** most actions have a shortcut ([list](docs/features.md#keyboard-shortcuts), or `?` in the UI). Single-key
  shortcuts only work when no button, link or field has focus, and can be turned off in Settings.
- **Focus:** a focused control that re-renders or disappears hands its focus to its equivalent, so focus doesn't drop
  to the page body. Run actions stay focusable during the run they started.
- **Components** follow the [WAI-ARIA Authoring Practices](https://www.w3.org/WAI/ARIA/apg/) patterns (design system
  README, [conventions](ui/design-system/README.md#conventions)).
- **Status messages:** the suite status and jumps between failures are announced through polite live regions.
- **Contrast:** every theme family is held to at least 4.5:1 for text and 3:1 for icons, the focus ring and other UI
  elements ([themes](docs/themes.md)).
- **Motion:** follows the operating system's reduce-motion setting. Settings › Appearance › Motion can reduce it or
  turn it off ([rules](ui/MOTION.md)).

## What is checked

The commands run in `ui/` ([CONTRIBUTING.md](CONTRIBUTING.md#checks-in-ui)).

| Check | Covers | Fails on | Runs |
| --- | --- | --- | --- |
| `pnpm run check:a11y`, gallery (`scripts/ds-axe.mjs`) | axe-core on the design-system gallery: every theme family, light and dark; open menu, dialog and tooltip in two themes | Any violation | CI, every PR |
| `pnpm run check:a11y`, app (`scripts/shell-axe.mjs`) | axe-core on the main page with the mock backend: every theme family, light and dark; default, test selected, watch mode, snapshot panel | Serious or critical violations | CI, every PR |
| `pnpm run check:keyboard` | Menu, tabs, dialog, split pane, switch, segmented control and tooltip in the gallery | Any failed step | CI, every PR |
| `pnpm run check:themes` | Contrast of the token pairs of every theme family | Pair below the minimum | Locally, when changing a theme |

A passing check does not mean the UI is accessible. Automated rules cover only part of WCAG, and these run in Chromium
at 1440×900.

### Not evaluated so far

- Screen readers and other assistive technology: there are no recorded test results for any combination.
- Pages other than the main page in the app-level axe check: settings and coverage, for example.
- The HTML report: it has labels, landmarks and a live region, but no automated check.
- Browsers other than Chromium, narrow viewports, browser zoom and text spacing.
- Testing with people with disabilities.

## Known limitations

| Barrier | Who is affected | What to do instead |
| --- | --- | --- |
| The live console is not an `aria-live` region. It announces once when a test finishes ("Test finished; N lines of live output."). | Screen reader users following output during a test | Read the output in the console after the announcement |
| Forced colors (Windows High Contrast) are handled in one place in the settings page and nowhere else, and have not been evaluated. | People using forced colors | None known; please report what breaks |

This list only holds what is known. No entry doesn't mean no barrier, so please report what you run into.

## Reporting a barrier

Open a [bug report](https://github.com/eriklieben/labcoat/issues/new?template=bug_report.yml) and put "Accessibility"
in the title. If the form gets in your way, start a [discussion](https://github.com/eriklieben/labcoat/discussions)
instead. Issues about accessibility get the `accessibility` label.

You don't have to say whether you have a disability. Useful to include: what you were trying to do, what happened, your
browser and operating system, and any assistive technology with its version. These reports are public, so leave out
anything private.

Labcoat has no fixed response time for these reports; status updates go on the issue.

## Contributing

Read this before changing UI code, as a person or as a coding agent:

- **Native first.** Use native elements, then the design-system components, and add ARIA only when those can't express
  the behavior. New reusable UI goes into the design system with a gallery section, so the checks above see it.
- **Names come from content.** The accessible name starts with the visible text; extra information goes in a
  `.visually-hidden` span after it, and decorative parts get `aria-hidden` ([details](ui/ARCHITECTURE.md)).
- **Keep focus.** Use `keepFocus` when a region re-renders, soft-disable a control that started something, and return
  focus when a popup closes.
- **Tokens only.** Colors and motion come from `--lc-*` tokens, so contrast and the motion settings keep working. Don't
  use color as the only signal for a test outcome.
- **Run the checks** in the table for what you changed, and look at it in light and dark mode.
- **Don't suppress a finding** to make a check pass: fix it, or say in the PR why it can't be fixed.
- **Say what was tested.** In the PR, name the checks you ran and what you tried by hand. Never report a test with
  assistive technology that didn't happen, and don't turn a passing check into a claim of conformance.

When a change alters what is listed here, update this file in the same PR.

---

The structure of this file is adapted from the [ACCESSIBILITY.md](https://github.com/mgifford/ACCESSIBILITY.md) template
(MIT License).
