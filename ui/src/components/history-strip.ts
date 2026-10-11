/**
 * <lc-history-strip> (port of HistoryStrip.tsx): recent results (oldest left, latest right), one square per run,
 * colored by outcome, plus a duration sparkline. Hovering a square or a sparkline point highlights the same run in
 * both and shows its readout. The square of the run being viewed (or the latest, when live) is outlined; clicking a
 * square switches the app to that run.
 *
 * Additions: squares of runs that are no longer stored (pruned from the run list) are not clickable and say so (P1-9);
 * an aggregated repeat-run entry with mixed results (`flaky`) gets a striped square.
 */
import { canAnimate, computed, cx, define, EASE, effect, h, LabcoatElement, list, MOTION, onCleanup, rovingIndex, signal, STATUS_LABEL, type Child } from "@eriklieben/labcoat-design-system";
import type { HistoryEntry } from "../contract";
import { getStore } from "../store";
import { formatDuration, formatRunTime } from "../util";
import { isRunStored } from "./run-diff";
import "./history-strip.css";

const describe = (e: HistoryEntry) => {
  const duration = e.durationMs != null && e.outcome !== "skipped" ? ` · ${formatDuration(e.durationMs)}` : "";
  return `${formatRunTime(e.at)} · ${STATUS_LABEL[e.outcome]}${e.flaky ? " (flaky)" : ""}${duration}`;
};

export interface HistoryStripProps {
  history: HistoryEntry[];
  /** run being viewed (undefined = live: the latest square is current) */
  activeRunId: string | undefined;
  onPick: ((runId: string | undefined, fallback?: { startedAt?: string }) => void) | undefined;
}
export interface HistoryStrip extends HistoryStripProps {}

export class HistoryStrip extends LabcoatElement<HistoryStripProps> {
  static defaults: HistoryStripProps = { history: [], activeRunId: undefined, onPick: undefined };
  static hostClass = "history";

  protected render(): Child {
    const p = this.props;
    const store = getStore();
    const hover = signal<number | undefined>(undefined);
    const currentIdx = computed(() => {
      const hist = p.history();
      const a = p.activeRunId();
      return a ? hist.findIndex((e) => e.runId === a) : hist.length - 1;
    });
    // the square in the tab order: the current one, else the latest clickable one
    const tabStop = computed(() => {
      const hist = p.history();
      const cur = currentIdx();
      const clickable = (i: number) => !!p.onPick() && !!hist[i]?.runId && (isRunStored(hist[i].runId) || store.$.runs().length === 0 || (i === hist.length - 1 && !p.activeRunId()));
      if (cur >= 0 && clickable(cur)) return cur;
      for (let i = hist.length - 1; i >= 0; i--) if (clickable(i)) return i;
      return -1;
    });
    const leave = () => hover.set(undefined);
    this.addEventListener("mouseleave", leave);
    onCleanup(() => this.removeEventListener("mouseleave", leave));

    const squares = h(
      "div",
      {
        class: "history-squares",
        role: "group",
        "aria-label": "Recent results, oldest first",
        // one tab stop (the current square); ←/→ / Home / End move between the clickable squares
        onkeydown: (e: KeyboardEvent) => {
          const els = [...(e.currentTarget as HTMLElement).querySelectorAll<HTMLButtonElement>(".hsq")];
          const i = rovingIndex(e, els.length, els.indexOf(e.target as HTMLButtonElement), "horizontal", (j) => els[j].disabled);
          if (i === undefined) return;
          e.preventDefault();
          els[i].focus();
        },
      },
      list(
        p.history,
        (_e, i) => i,
        (entry, index) => {
          const isLatest = () => index() === p.history().length - 1;
          // a run pruned from the persisted list can't be viewed (an old host without a run list: everything clickable)
          const pruned = () => !!entry().runId && store.$.runs().length > 0 && !isRunStored(entry().runId) && !(isLatest() && !p.activeRunId());
          const label = () => {
            const e = entry();
            if (index() !== currentIdx()) return describe(e);
            return `${describe(e)}${p.activeRunId() ? " (viewing)" : " (latest)"}`;
          };
          return h("button", {
            class: () =>
              cx("hsq", `hsq-${entry().outcome}`, entry().flaky && "hsq-flaky", isLatest() && "latest", index() === currentIdx() && "current", hover() === index() && "hover", pruned() && "pruned"),
            title: () => {
              if (pruned()) return `${label()}\nNo longer stored`;
              return p.onPick() && entry().runId ? `${label()}\nClick to view this run` : label();
            },
            "aria-label": label,
            "aria-current": () => (index() === currentIdx() ? "true" : null),
            tabIndex: () => (index() === tabStop() ? 0 : -1),
            disabled: () => !p.onPick() || !entry().runId || pruned(),
            onmouseenter: () => hover.set(index.peek()),
            onfocus: () => hover.set(index.peek()),
            onclick: () => {
              const e = entry.peek();
              const onPick = p.onPick.peek();
              if (!onPick || !e.runId) return;
              const active = p.activeRunId.peek();
              // The latest square while live is "now": clicking it stays live; clicking the viewed run again goes back.
              if ((!active && index.peek() === p.history.peek().length - 1) || e.runId === active) onPick(undefined);
              else onPick(e.runId, { startedAt: e.at });
            },
          });
        },
      ),
    );

    // P17: a new result for this test: the strip shifts left by one square (when capped), the new square pops in and
    // the sparkline's last dot fades in. Decorative: not on first render, not when busy / reduced motion.
    let lastAt: string | undefined;
    let lastLen = -1;
    effect(() => {
      const hist = p.history();
      const at = hist.at(-1)?.at;
      const grew = hist.length > lastLen;
      const changed = lastLen >= 0 && at !== lastAt && hist.length > 0;
      lastAt = at;
      lastLen = hist.length;
      if (!changed || !canAnimate("decorative")) return;
      queueMicrotask(() => {
        const sq = squares.querySelectorAll<HTMLElement>(".hsq");
        const newest = sq[sq.length - 1];
        if (!grew) squares.animate([{ transform: "translateX(12px)" }, { transform: "none" }], { duration: MOTION.base, easing: EASE.emphasized });
        newest?.animate([{ transform: "scale(0)", opacity: 0 }, { transform: "none", opacity: 1 }], { duration: MOTION.enter, easing: EASE.decelerate });
        spark.querySelector(".spark-dot")?.animate([{ opacity: 0 }, { opacity: 1 }], { duration: MOTION.base, easing: EASE.standard });
      });
    });

    const spark = h("span", { class: "sparkline-host" });
    const sparkHtml = computed(() => sparkline(p.history(), hover() ?? (p.activeRunId() && currentIdx() >= 0 ? currentIdx() : undefined)));
    let last = "";
    effect(() => {
      const s = sparkHtml();
      if (s !== last) spark.innerHTML = last = s;
    });
    spark.addEventListener("mousemove", (e) => {
      const svg = spark.querySelector("svg");
      const n = p.history.peek().length;
      if (!svg || n < 2) return;
      const r = svg.getBoundingClientRect();
      const x = ((e.clientX - r.left) / r.width) * W;
      hover.set(Math.max(0, Math.min(n - 1, Math.round(((x - PAD) / (W - 2 * PAD)) * (n - 1)))));
    });

    return [
      squares,
      spark,
      // not a live region: it follows the mouse over the sparkline (each square has its own label)
      h("span", { class: "history-readout mono", "aria-hidden": "true" }, () => {
        const hist = p.history();
        const hv = hover();
        const cur = currentIdx();
        if (hv != null && hist[hv]) return describe(hist[hv]);
        if (p.activeRunId() && cur >= 0) return describe(hist[cur]);
        return `${hist.length} run${hist.length === 1 ? "" : "s"}`;
      }),
    ];
  }
}
define("lc-history-strip", HistoryStrip);

declare global {
  interface HTMLElementTagNameMap {
    "lc-history-strip": HistoryStrip;
  }
}

const W = 132;
const H = 28;
const PAD = 4;

function sparkline(history: HistoryEntry[], hover: number | undefined): string {
  const pts = history
    .map((e, i) => ({ i, e, d: e.outcome === "skipped" ? undefined : e.durationMs }))
    .filter((x): x is { i: number; e: HistoryEntry; d: number } => x.d != null);
  if (pts.length < 2) return "";
  const max = Math.max(...pts.map((x) => x.d));
  const min = Math.min(...pts.map((x) => x.d));
  const n = history.length;
  const x = (i: number) => PAD + (n === 1 ? 0 : (i / (n - 1)) * (W - 2 * PAD));
  const y = (d: number) => (max === min ? H / 2 : PAD + (1 - (d - min) / (max - min)) * (H - 2 * PAD));
  const line = pts.map((q, k) => `${k ? "L" : "M"}${x(q.i).toFixed(1)},${y(q.d).toFixed(1)}`).join(" ");
  const area = `${line} L${x(pts.at(-1)!.i).toFixed(1)},${H} L${x(pts[0].i).toFixed(1)},${H} Z`;
  const hp = hover != null ? pts.find((q) => q.i === hover) : undefined;
  const dot = hp ?? pts.at(-1)!;
  const label = `Duration over the last ${pts.length} runs: ${formatDuration(min)} to ${formatDuration(max)}`;
  return (
    `<svg class="sparkline" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="${label}">` +
    `<path class="spark-area" d="${area}"/><path class="spark-line" d="${line}"/>` +
    (hp ? `<line class="spark-cross" x1="${x(hp.i)}" x2="${x(hp.i)}" y1="0" y2="${H}"/>` : "") +
    `<circle class="spark-dot hsq-${dot.e.outcome}" cx="${x(dot.i)}" cy="${y(dot.d)}" r="3"/></svg>`
  );
}
