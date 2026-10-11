/**
 * Property grid: label/value rows in a bordered two-column grid (the detail pane's Status / Duration / Last run).
 *
 *   propertyGrid([
 *     { name: "Status", value: statusBadge("passed") },
 *     { name: "Duration", value: () => formatMs(test().durationMs) },
 *     { name: "Tags", value: [badge({}, "smoke"), badge({}, "db")], badges: true },
 *   ], { label: "Test properties" })
 *
 * Markup: `<dl class="prop-grid" aria-label><dt class="prop-name"/><dd class="prop-value [badges]"/>…</dl>` (a
 * description list; same classes as the React app's divs). Pass `rows` as an accessor when rows come and go; the
 * grid is rebuilt when the row names change, values stay reactive.
 */
import { h, keyed, read, type Child, type ClassValue, type MaybeReactive } from "../core";

export interface PropertyRow {
  name: string;
  value: Child;
  /** wrap value children with a gap (badges, chips) */
  badges?: boolean;
  /** mono value */
  mono?: boolean;
}

export interface PropertyGridOptions {
  label?: string;
  class?: ClassValue;
}

export function propertyGrid(rows: MaybeReactive<readonly PropertyRow[]>, opts: PropertyGridOptions = {}): HTMLDListElement {
  const renderRows = (rs: readonly PropertyRow[]) =>
    rs.map((r) => [h("dt", { class: "prop-name" }, r.name), h("dd", { class: ["prop-value", r.badges && "badges", r.mono && "mono"] }, r.value)]);
  return h(
    "dl",
    { class: ["prop-grid", opts.class], "aria-label": opts.label },
    typeof rows === "function" ? keyed(() => read(rows).map((r) => r.name).join("\u0000"), () => renderRows(read(rows))) : renderRows(rows),
  );
}
