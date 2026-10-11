/**
 * Failure message with an expected/actual diff (port of DiffView.tsx). Factories, not elements: they are small and
 * rendered once per failure (the detail pane rebuilds them when the message changes).
 *
 *   failureMessage(message)   summary + diff + "Raw message" disclosure, or the raw message when nothing parses
 *   assertionDiff(parsed)     the diff block alone
 */
import { h, icon, signal, type Child } from "@eriklieben/labcoat-design-system";
import { buildDiff, foldContext, parseAssertion, type DiffLine, type DiffModel, type Op, type ParsedAssertion, type Segment } from "../diff";
import "./diff-view.css";

export function failureMessage(message: string): HTMLElement {
  const parsed = parseAssertion(message);
  if (!parsed) return h("pre", { class: "mb-message" }, message);
  const raw = signal(false);
  return h(
    "div",
    { class: "failure-message" },
    parsed.summary ? h("pre", { class: "mb-message" }, parsed.summary) : null,
    assertionDiff(parsed),
    h(
      "button",
      { class: "raw-toggle", "aria-expanded": () => raw(), onclick: () => raw.update((v) => !v) },
      () => icon(raw() ? "chevronDown" : "chevronRight", { size: 12 }),
      " Raw message",
    ),
    () => (raw() ? h("pre", { class: "mb-message raw-message" }, message) : null),
  );
}

export function assertionDiff(parsed: ParsedAssertion): HTMLElement {
  const model = buildDiff(parsed.expected, parsed.actual, parsed.cut);
  return h(
    "div",
    { class: `diff-block diff-kind-${model.kind}` },
    diffHead(model, parsed.truncated),
    model.kind === "inline"
      ? h(
          "div",
          { class: "diff-inline mono" },
          h("span", { class: "diff-label exp" }, "Expected"),
          value(model.expected, "exp", parsed.quoted, parsed.cut?.expected.start, parsed.cut?.expected.end),
          h("span", { class: "diff-label act" }, "Actual"),
          value(model.actual, "act", parsed.quoted, parsed.cut?.actual.start, parsed.cut?.actual.end),
        )
      : lineDiffView(model.lines),
  );
}

/** Legend + counts of a line diff, and the "partial values" note; null when there is neither. */
function diffHead(model: DiffModel, truncated: boolean): HTMLElement | null {
  if (model.kind !== "lines" && !truncated) return null;
  return h(
    "div",
    { class: "diff-head" },
    model.kind === "lines" ? diffLegend(model) : null,
    truncated
      ? h(
          "span",
          { class: "diff-note", title: "The test framework only reported a window of the values around the first difference" },
          icon("info", { size: 12 }),
          " Partial values",
        )
      : null,
  );
}

function diffLegend(model: Extract<DiffModel, { kind: "lines" }>): Child[] {
  return [
    h("span", { class: "diff-legend exp" }, h("span", { class: "diff-sign" }, "−"), " Expected"),
    h("span", { class: "diff-legend act" }, h("span", { class: "diff-sign" }, "+"), " Actual"),
    h(
      "span",
      { class: "diff-stats mono" },
      model.json ? h("span", { class: "diff-tag" }, "JSON") : null,
      h("span", { class: "exp" }, `−${model.removed}`),
      " ",
      h("span", { class: "act" }, `+${model.added}`),
    ),
  ];
}

/** Make changed whitespace visible (otherwise a highlighted space is just a colored gap). */
const visibleWs = (s: string) => (/^\s+$/.test(s) ? s.replaceAll(" ", "·").replaceAll("\t", "→").replaceAll("\n", "↵") : s);

const segs = (list: Segment[]): Child[] => list.map((s) => (s.changed ? h("mark", null, visibleWs(s.text)) : h("span", null, s.text)));

function value(list: Segment[], side: "exp" | "act", quoted: boolean, cutStart?: boolean, cutEnd?: boolean) {
  return h(
    "span",
    { class: `diff-value ${side}` },
    quoted ? h("span", { class: "diff-quote" }, '"') : null,
    cutStart ? h("span", { class: "diff-cut" }, "…") : null,
    list.length === 0 ? h("span", { class: "diff-empty" }, "(empty)") : null,
    segs(list),
    cutEnd ? h("span", { class: "diff-cut" }, "…") : null,
    quoted ? h("span", { class: "diff-quote" }, '"') : null,
  );
}

/** Unified line diff (folded context). `labels` name the two sides for screen readers (snapshots: verified / received). */
const ASSERT_LABELS = { del: "expected", ins: "actual" };

export function lineDiffView(lines: DiffLine[], labels: { del: string; ins: string } = ASSERT_LABELS) {
  const rows = foldContext(lines);
  const open = signal<ReadonlySet<number>>(new Set());
  return h(
    "div",
    // a list of lines (not a table: rows had no cells, axe aria-required-children); each line says expected / actual
    { class: "diff-lines mono", role: "list", "aria-label": `Line diff: ${labels.del} (−) and ${labels.ins} (+)` },
    () =>
      rows.map((r) => {
        if ("fold" in r) {
          if (open().has(r.from)) return lines.slice(r.from, r.from + r.fold).map((l) => line(l, labels));
          return h(
            "div",
            { class: "diff-fold-item", role: "listitem" },
            h(
              "button",
              { class: "diff-fold", onclick: () => open.update((s) => new Set(s).add(r.from)) },
              icon("chevronDown", { size: 12 }),
              ` ${r.fold} unchanged line${r.fold === 1 ? "" : "s"}`,
            ),
          );
        }
        return line(r, labels);
      }),
  );
}

const LINE_SIDE: Record<Op, string> = { del: "exp", ins: "act", eq: "" };
const LINE_SIGN: Record<Op, string> = { del: "−", ins: "+", eq: "" };

function line(l: DiffLine, labels: { del: string; ins: string }) {
  return h(
    "div",
    { class: `diff-line ${LINE_SIDE[l.op]}`, role: "listitem" },
    h("span", { class: "diff-ln", "aria-hidden": "true" }, l.a != null ? String(l.a) : ""),
    h("span", { class: "diff-ln", "aria-hidden": "true" }, l.b != null ? String(l.b) : ""),
    h("span", { class: "diff-sign", "aria-hidden": "true" }, LINE_SIGN[l.op]),
    l.op === "eq" ? null : h("span", { class: "visually-hidden" }, `${labels[l.op]}: `),
    h("span", { class: "diff-text" }, l.segments ? segs(l.segments) : l.text || " "),
  );
}
