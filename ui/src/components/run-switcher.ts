/**
 * <lc-run-switcher>: header pill to switch between the live state and a past run (port of RunSwitcher.tsx, Aspire
 * dashboard style: "● Live" + a menu of past runs). Picking a past run shows the Tests page as a read-only snapshot
 * (store.viewRun). Reads the store and settings directly.
 *
 * Additions over the React version:
 * - the pill says "Offline" / "Connecting…" when not connected ("Live" only while connected) (P2-2)
 * - entries show trigger, scope, duration and what changed vs. the previous results ("+2 failed", "1 fixed"); runs in
 *   which a failure first appeared are highlighted; partial runs only with Settings → partial runs in history (P1-9)
 * - "Compare with…" (pick a second run) opens the run diff sheet <lc-run-diff> (F2)
 */
import { animateEnter, computed, cx, define, effect, h, icon, keyed, LabcoatElement, list, MOTION, onCleanup, popover, replayClass, show, signal, untracked, type Child, type IconName } from "@eriklieben/labcoat-design-system";
import type { RunSummary } from "../contract";
import { connectionOf, getStore, type Connection } from "../store";
import { settings } from "../settings";
import { formatDuration, formatRunTime, runCounts } from "../util";
import { closeCompare, comparison, comparisonOpen, LIVE, openCompare, runDeltas, runRefLabel, type RunDelta } from "./run-diff";
import { affectedRunLabel } from "./run-affected";
import { exportRun, RUN_EXPORTS } from "../export";
import "./run-switcher.css";

function runTone(r: RunSummary): "failed" | "passed" | "other" {
  if (r.failed > 0) return "failed";
  return r.passed + r.skipped >= r.total && r.total > 0 ? "passed" : "other";
}

/** "1 test", "3 tests" */
const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;

/** The connection as the pill label, the menu's Live row and the pill's tooltip say it. */
const CONN_TEXT: Record<Connection, { pill: string; menu: string; title: string }> = {
  live: { pill: "Live", menu: "Live", title: "Connected: showing live results" },
  offline: { pill: "Offline", menu: "Live (offline)", title: "Not connected to the Labcoat host: results may be stale" },
  connecting: { pill: "Connecting…", menu: "Live (connecting)", title: "Connecting to the Labcoat host…" },
};

/** The menu item a navigation key moves to: first / last, or one step (from before the first when none has focus). */
function menuTarget(step: number, at: number, n: number): number {
  if (step === -Infinity) return 0;
  if (step === Infinity) return n - 1;
  return Math.max(0, Math.min(n - 1, (at < 0 ? -1 : at) + step));
}

const TRIGGERS: Record<string, { icon: IconName; label: string; title: string }> = {
  user: { icon: "play", label: "Run", title: "Started from Labcoat" },
  watch: { icon: "eye", label: "Watch", title: "Started by watch mode (a file changed)" },
  api: { icon: "open", label: "API", title: "Started through the REST API (e.g. an Aspire dashboard command)" },
  dashboard: { icon: "open", label: "Dashboard", title: "Started from the Aspire dashboard" },
  debug: { icon: "bug", label: "Debug", title: "A debug run (waited for a debugger to attach)" },
  cli: { icon: "console", label: "CLI", title: "Started by labcoat --run (e.g. on CI)" },
};

/** The trigger chip of a run: "Affected" (run-affected.ts, RunSummary.affected strategy "git") or TRIGGERS. */
function triggerOf(r: RunSummary): { icon: IconName; label: string; title: string } {
  const affected = affectedRunLabel(r);
  if (affected) return { icon: "branch", label: "Affected", title: `${affected}: the tests affected by the changes (git)` };
  return TRIGGERS[r.trigger ?? "user"] ?? TRIGGERS.user;
}

/** Ask the run switcher to open its menu in "Compare with…" mode (e.g. from the past-run message bar). */
export function requestCompare() {
  window.dispatchEvent(new CustomEvent("labcoat:compare-request"));
}

interface Entry {
  run: RunSummary;
  delta?: RunDelta;
  /** summary-based fallback: change in the failed count vs. the previous listed full run */
  failedDelta?: number;
}

export interface RunSwitcherProps {
  /** select a tree node (key `t:<projectId>|<id>`) on the Tests page; used by the run diff's rows */
  onJump: (key: string) => void;
}
export interface RunSwitcher extends RunSwitcherProps {}
export class RunSwitcher extends LabcoatElement<RunSwitcherProps> {
  static defaults: RunSwitcherProps = { onJump: () => {} };
  static hostClass = "run-switcher";

  /** menu position (fixed: the header clips overflow); undefined = closed */
  readonly #open = signal<{ top: number; right: number } | undefined>(undefined);
  /** "Compare with…": the next pick opens the diff against the shown run (live or viewed) */
  readonly #picking = signal(false);

  protected render(): Child {
    const store = getStore();
    const $ = store.$;
    let pill!: HTMLButtonElement;

    const conn = computed(() => connectionOf({ conn: $.conn(), everConnected: $.everConnected() }));
    const viewing = computed(() => $.viewing()?.run);
    const current = () => viewing()?.runId ?? LIVE;

    const entries = computed<Entry[]>(() => {
      const all = settings().partialRunsInHistory ? $.runs() : $.runs().filter((r) => r.scope !== "subset");
      if (!this.#open()) return all.map((run) => ({ run })); // deltas only while the menu shows them
      const deltas = runDeltas();
      return all.map((run, i) => {
        const delta = deltas.get(run.runId);
        if (delta || run.scope === "subset") return { run, delta };
        const prev = all.slice(i + 1).find((r) => r.scope !== "subset");
        return { run, failedDelta: prev ? run.failed - prev.failed : undefined };
      });
    });

    const close = (focusPill = false) => {
      this.#open.set(undefined);
      this.#picking.set(false);
      if (focusPill) pill.focus({ preventScroll: true });
    };
    const openMenu = (picking = false) => {
      const r = pill.getBoundingClientRect();
      this.#picking.set(picking);
      this.#open.set({ top: r.bottom + 6, right: Math.max(8, window.innerWidth - r.right) });
    };
    const pick = (id: string | undefined) => {
      if (this.#picking.peek()) {
        const other = viewing.peek()?.runId ?? LIVE;
        close();
        if ((id ?? LIVE) !== other) openCompare(id ?? LIVE, other);
        return;
      }
      close(true);
      store.viewRun(id);
    };

    const onCompareRequest = () => openMenu(true);
    window.addEventListener("labcoat:compare-request", onCompareRequest);
    onCleanup(() => window.removeEventListener("labcoat:compare-request", onCompareRequest));

    // While open: resize, Escape (captured, so the app's Escape = back to live doesn't fire).
    effect(() => {
      if (!this.#open()) return;
      const onKey = (e: KeyboardEvent) => {
        if (e.key !== "Escape") return;
        e.stopPropagation();
        if (this.#picking.peek()) this.#picking.set(false);
        else close(true);
      };
      const onResize = () => close();
      window.addEventListener("resize", onResize);
      window.addEventListener("keydown", onKey, true);
      onCleanup(() => {
        window.removeEventListener("resize", onResize);
        window.removeEventListener("keydown", onKey, true);
      });
    });
    // Focus the checked item when the menu opens / the mode changes.
    effect(() => {
      if (!this.#open()) return;
      this.#picking();
      queueMicrotask(() => {
        const items = this.items();
        (items.find((x) => x.getAttribute("aria-checked") === "true" && !x.disabled) ?? items[0])?.focus({ preventScroll: true });
      });
    });

    const onMenuKey = (e: KeyboardEvent) => {
      if (e.key === "Tab") return close();
      const nav: Record<string, number> = { ArrowDown: 1, ArrowUp: -1, Home: -Infinity, End: Infinity };
      if (!(e.key in nav)) return;
      e.preventDefault();
      const items = this.items();
      const i = items.indexOf(document.activeElement as HTMLButtonElement);
      items[menuTarget(nav[e.key], i, items.length)]?.focus();
    };

    // P15: connecting → the dot pulses; offline → a static grey dot (a pulse would suggest activity); back to live →
    // one ripple (600 ms) — the "we're back" signal. The pill label crossfades between states (120 ms).
    let wasLive = untracked(conn) === "live";
    let wasDown = false;
    const reconnected = signal(0);
    effect(() => {
      const c = conn();
      if (c !== "live") wasDown = wasDown || wasLive;
      else if (wasDown) {
        wasDown = false;
        untracked(() => reconnected.update((n) => n + 1));
      }
      wasLive = c === "live";
    });
    const liveDot = () =>
      h("span", {
        class: () => cx("run-dot", "live", conn() !== "live" && "off", conn() === "offline" && "offline", conn() === "connecting" && "lc-pulse"),
        ref: (el) =>
          effect(() => {
            if (reconnected()) untracked(() => replayClass(el, "ripple", MOTION.xslow));
          }),
      });
    const connTitle = () => CONN_TEXT[conn()].title;

    const runItem = (e: () => Entry) => {
      const r = () => e().run;
      const sel = () => !this.#picking() && viewing()?.runId === r().runId;
      const isOther = () => this.#picking() && current() === r().runId;
      return h(
        "button",
        {
          class: () => cx("run-menu-item", "run-menu-run", (e().delta?.newFailures ?? 0) > 0 && "first-fail"),
          role: "menuitemradio",
          "aria-checked": sel,
          disabled: isOther,
          onclick: () => pick(r().runId),
          title: () => runTitle(e()),
          // one sentence instead of the mashed cell text ("Yesterday 10:21 PM1582+1 failed…")
          "aria-label": () => runAriaLabel(e()),
        },
        h("span", { class: "run-check" }, show(sel, () => icon("checkmark", { size: 16 }))),
        h("span", { class: () => cx("run-dot", `tone-${runTone(r())}`) }),
        h(
          "span",
          { class: "run-menu-main" },
          h(
            "span",
            { class: "run-menu-line" },
            h("span", { class: "run-menu-time" }, () => formatRunTime(r().startedAt)),
            h(
              "span",
              { class: "run-menu-counts mono" },
              h("span", { class: "c-passed" }, () => r().passed),
              show(
                () => r().failed > 0,
                () => h("span", { class: "c-failed" }, () => r().failed),
              ),
              show(
                () => r().skipped > 0,
                () => h("span", { class: "c-skipped" }, () => r().skipped),
              ),
            ),
            h("span", { class: "run-delta" }, () => deltaChips(e())),
          ),
          h(
            "span",
            { class: "run-menu-sub" },
            () => {
              const t = triggerOf(r());
              return h("span", { class: "run-trigger", title: t.title }, icon(t.icon, { size: 12 }), t.label);
            },
            h("span", { class: "run-sub-sep", "aria-hidden": "true" }, "·"),
            h("span", { class: () => cx("run-scope", r().scope === "subset" && "partial") }, () =>
              r().scope === "subset" ? `Partial · ${plural(r().total, "test")}` : `Full · ${r().total} tests`,
            ),
            show(
              () => r().durationMs != null,
              () => [h("span", { class: "run-sub-sep", "aria-hidden": "true" }, "·"), h("span", { class: "mono" }, () => formatDuration(r().durationMs))],
            ),
          ),
        ),
      );
    };

    // The menu is the design system's popover (P4: grows from the pill, origin top right; 80 ms fade out; outside
    // click closes it). It is portaled next to the header, so items are looked up through #menu.
    const menuEl = () =>
      h(
        "div",
        {
          class: "run-menu-body",
          role: "menu",
          "aria-label": () => (this.#picking() ? "Compare with" : "Runs"),
          onkeydown: onMenuKey,
        },
        show(
          () => this.#picking(),
          () =>
            h(
              "div",
              { class: "run-menu-pickhead" },
              h(
                "button",
                { class: "run-menu-item run-menu-back", role: "menuitem", onclick: () => this.#picking.set(false), title: "Back (Esc)" },
                h("span", { class: "run-check" }, icon("arrowLeft", { size: 16 })),
                h("span", { class: "run-menu-pick-title" }, () => `Compare ${runRefLabel(current(), $.runs())} with…`),
              ),
            ),
        ),
        h(
          "button",
          {
            class: "run-menu-item",
            role: "menuitemradio",
            "aria-checked": () => !this.#picking() && !viewing(),
            disabled: () => this.#picking() && !viewing(),
            onclick: () => pick(undefined),
          },
          h(
            "span",
            { class: "run-check" },
            show(
              () => !this.#picking() && !viewing(),
              () => icon("checkmark", { size: 16 }),
            ),
          ),
          liveDot(),
          h("span", { class: "run-menu-time" }, () => CONN_TEXT[conn()].menu),
          h("span", { class: "run-menu-meta dim" }, "current results"),
        ),
        show(
          () => !this.#picking(),
          () => [
            h(
              "button",
              {
                class: "run-menu-item run-menu-action",
                role: "menuitem",
                disabled: () => $.runs().length === 0,
                onclick: () => this.#picking.set(true),
                title: () => `Compare ${viewing() ? "this run" : "the live results"} with another run: new failures, fixed, newly slow…`,
              },
              h("span", { class: "run-check" }),
              icon("arrowSync", { size: 16, class: "run-menu-action-icon" }),
              h("span", { class: "run-menu-time" }, "Compare with…"),
            ),
            // Export: the shown run (the viewed past run, else the latest stored run) as a file (export.ts)
            ...RUN_EXPORTS.map((x) =>
              h(
                "button",
                {
                  class: "run-menu-item run-menu-action",
                  role: "menuitem",
                  disabled: () => $.runs().length === 0,
                  onclick: () => {
                    const runId = viewing.peek()?.runId;
                    close(true);
                    void exportRun(runId, x.format);
                  },
                  title: () => `Download ${viewing() ? "this run" : "the latest run"} as ${x.label}: ${x.detail}`,
                },
                h("span", { class: "run-check" }),
                icon("arrowDownload", { size: 16, class: "run-menu-action-icon" }),
                h("span", { class: "run-menu-time" }, `Export as ${x.label}`),
              ),
            ),
            show(
              () => !!comparison() && !comparisonOpen(),
              () =>
                h(
                  "button",
                  {
                    class: "run-menu-item run-menu-action",
                    role: "menuitem",
                    onclick: () => {
                      close();
                      comparisonOpen.set(true);
                    },
                  },
                  h("span", { class: "run-check" }),
                  icon("history", { size: 16, class: "run-menu-action-icon" }),
                  h("span", { class: "run-menu-time" }, () => {
                    const c = comparison()!;
                    return `Show comparison ${runRefLabel(c.base, $.runs())} → ${runRefLabel(c.target, $.runs())}`;
                  }),
                ),
            ),
          ],
        ),
        h("div", { class: "run-menu-sep", role: "separator" }),
        h(
          "div",
          { class: "run-menu-list" },
          show(
            () => entries().length === 0,
            () => h("div", { class: "run-menu-empty" }, () => ($.runs().length ? "Only partial runs so far (Settings → History)" : "No past runs yet")),
          ),
          list(
            entries,
            (x) => x.run.runId,
            (x) => runItem(x),
          ),
        ),
        h("div", { class: "run-menu-sep", role: "separator" }),
        h(
          "div",
          { class: "run-menu-foot dim" },
          icon("info", { size: 14 }),
          h("span", () => {
            if (this.#picking()) return "Pick the run to compare with. The older run is the base.";
            const hidden = settings().partialRunsInHistory ? "" : "; partial runs are hidden (Settings)";
            return `The last 50 runs are kept per workspace${hidden}. Starting a run returns to Live; Esc too.`;
          }),
        ),
      );

    return [
      h(
        "button",
        {
          class: () => cx("run-pill", viewing() && "past", !viewing() && conn() !== "live" && "offline"),
          ref: (el) => (pill = el),
          onclick: () => (this.#open.peek() ? close() : openMenu()),
          onkeydown: (e) => {
            if (e.key === "ArrowDown" && !this.#open.peek()) {
              e.preventDefault();
              openMenu();
            }
          },
          "aria-haspopup": "menu",
          "aria-expanded": () => !!this.#open(),
          title: () => {
            const v = viewing();
            return v ? `Viewing the run from ${formatRunTime(v.startedAt)} (${runCounts(v)})` : `${connTitle()}. ${plural(entries().length, "stored run")}: pick one to view it`;
          },
        },
        keyed(
          () => (viewing() ? "past" : "live"),
          (k) => {
            if (k === "past")
              return [h("span", { class: () => cx("run-dot", `tone-${runTone(viewing()!)}`) }), h("span", { class: "run-pill-label" }, () => formatRunTime(viewing()?.startedAt))];
            return [
              liveDot(),
              keyed(conn, (c) => animateEnter(h("span", { class: "run-pill-label" }, CONN_TEXT[c].pill), "fade")),
              // the history behind the pill is visible: "Live · 14 runs"
              show(
                () => entries().length > 0,
                () => h("span", { class: "run-pill-count" }, () => `· ${plural(entries().length, "run")}`),
              ),
            ];
          },
        ),
        icon("chevronDown", { size: 12 }),
      ),
      popover({
        open: () => !!this.#open(),
        anchor: () => pill,
        placement: "bottom-end",
        offset: 6,
        role: null,
        initialFocus: "none",
        class: ["run-menu", "run-menu-rich", { picking: () => this.#picking() }],
        onClose: (reason) => close(reason === "escape"),
        ref: (el) => (this.#menu = el),
        content: menuEl,
      }),
      show(comparisonOpen, () =>
        h("lc-run-diff", {
          onJump: (key: string) => {
            closeCompare();
            this.onJump(`t:${key}`); // tree node key of a test leaf (tree.ts)
          },
          onClose: () => {
            closeCompare();
            pill.focus({ preventScroll: true });
          },
        }),
      ),
    ];
  }

  #menu: HTMLElement | undefined;
  private items() {
    return [...(this.#menu?.querySelectorAll<HTMLButtonElement>(".run-menu-item") ?? [])].filter((b) => !b.disabled);
  }
}

function deltaChips(e: Entry): Child {
  const d = e.delta;
  if (d) {
    const out: Child[] = [];
    if (d.newFailures) out.push(h("span", { class: "delta delta-bad", title: `${d.newFailures} test${d.newFailures === 1 ? "" : "s"} started failing in this run` }, `+${d.newFailures} failed`));
    if (d.fixed) out.push(h("span", { class: "delta delta-good", title: `${d.fixed} test${d.fixed === 1 ? "" : "s"} fixed in this run` }, `${d.fixed} fixed`));
    return out;
  }
  const f = e.failedDelta;
  if (!f) return null;
  return h("span", { class: cx("delta", f > 0 ? "delta-bad" : "delta-good"), title: "Change in failed tests vs. the previous full run" }, f > 0 ? `+${f} failed` : `${-f} fewer failed`);
}

function runAriaLabel(e: Entry) {
  const r = e.run;
  const t = triggerOf(r);
  return [
    formatRunTime(r.startedAt),
    `${t.label.toLowerCase()} run`,
    r.scope === "subset" ? `partial, ${plural(r.total, "test")}` : `full, ${r.total} tests`,
    runCounts(r),
    e.delta?.newFailures ? plural(e.delta.newFailures, "new failure") : "",
    e.delta?.fixed ? `${e.delta.fixed} fixed` : "",
    r.durationMs != null ? formatDuration(r.durationMs) : "",
  ]
    .filter(Boolean)
    .join(", ");
}

function runTitle(e: Entry) {
  const r = e.run;
  const t = triggerOf(r);
  const parts = [
    new Date(r.startedAt).toLocaleString(),
    runCounts(r),
    r.scope === "subset" ? `partial run (${r.total} selected)` : "full run",
    r.durationMs != null ? formatDuration(r.durationMs) : "",
    t.title,
  ];
  if (e.delta?.newFailures) parts.push(`${e.delta.newFailures} new failure${e.delta.newFailures === 1 ? "" : "s"}`);
  if (e.delta?.fixed) parts.push(`${e.delta.fixed} fixed`);
  return parts.filter(Boolean).join(" · ");
}

define("lc-run-switcher", RunSwitcher);

declare global {
  interface HTMLElementTagNameMap {
    "lc-run-switcher": RunSwitcher;
  }
}
