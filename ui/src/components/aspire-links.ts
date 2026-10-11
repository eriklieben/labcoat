/**
 * Links from a test to its telemetry in the Aspire dashboard (port of AspireLinks.tsx), plus the inline Aspire failure
 * context (F3): the error spans and warning/error logs of the failing test's trace (GET /api/tests/telemetry).
 *
 * Everything here is opt-in: shown only when Labcoat is hosted by Aspire (aspireDashboardUrl), trace collection is on
 * (state.tracingEnabled, off by default) and the test reported a trace id (Labcoat.Testing). No generic "Traces" link
 * for tests without a trace (P2-9).
 */
import gantt from "@fluentui/svg-icons/icons/gantt_chart_20_regular.svg?raw";
import bulletList from "@fluentui/svg-icons/icons/text_bullet_list_ltr_20_regular.svg?raw";
import { computed, effect, h, icon, onCleanup, signal, untracked, type Child } from "@eriklieben/labcoat-design-system";
import type { TelemetryLog, TelemetrySpan, TestNode, TestTelemetry } from "../contract";
import { getStore } from "../store";
import { formatDuration, trimTrailingSlashes } from "../util";
import "./aspire-links.css";

const svg = (raw: string) =>
  raw.replace(/<svg([^>]*)>/, (_m, attrs: string) => `<svg${attrs.replace(/\s(width|height)="[^"]*"/g, "")} fill="currentColor" aria-hidden="true" focusable="false">`);
const ICONS = { trace: svg(gantt), logs: svg(bulletList) };

export const glyph = (name: keyof typeof ICONS, size = 16) => h("span", { class: "icon", style: { width: size, height: size }, innerHTML: ICONS[name] });

export function dashboardLinks(base: string | undefined, traceId: string | undefined) {
  if (!base || !traceId) return undefined;
  const root = trimTrailingSlashes(base);
  return {
    trace: `${root}/traces/detail/${encodeURIComponent(traceId)}`,
    logs: `${root}/structuredlogs?traceId=${encodeURIComponent(traceId)}`,
  };
}

/** The test's dashboard links, or undefined when tracing is off / no dashboard / no trace id. Reactive. */
export function testLinks(test: () => TestNode | undefined) {
  const $ = getStore().$;
  return computed(() => {
    const t = test();
    return $.tracingEnabled() ? dashboardLinks($.aspireDashboardUrl(), t?.traceId) : undefined;
  });
}

/** Header actions: "View trace" + "Logs" for a traced test (nothing otherwise). */
export function aspireLinks(test: () => TestNode): Child {
  const links = testLinks(test);
  return () => {
    const l = links();
    if (!l) return null;
    return [
      h(
        "a",
        { class: "btn", href: l.trace, target: "_blank", rel: "noreferrer", title: `View this test's trace in the Aspire dashboard (${test().traceId})` },
        glyph("trace"),
        " View trace",
      ),
      h("a", { class: "btn", href: l.logs, target: "_blank", rel: "noreferrer", title: "Structured logs of this test's trace in the Aspire dashboard" }, glyph("logs"), " Logs"),
    ];
  };
}

// ---- failure context (F3) ---------------------------------------------------------------------------------

type Telemetry = { status: "loading" } | { status: "ready"; data: TestTelemetry } | { status: "error"; message: string };

async function fetchTelemetry(t: TestNode, signal: AbortSignal): Promise<TestTelemetry> {
  // the mock backend is loaded on demand (?mock only): never part of the production chunk
  if (getStore().get().transport === "mock") return (await import("../mock/detail")).mockTelemetry(t, signal);
  const q = new URLSearchParams({ projectId: t.projectId, id: t.id });
  const res = await fetch(new URL(`./api/tests/telemetry?${q}`, location.href), { signal });
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { error?: string };
    throw new Error(body.error || `${res.status} ${res.statusText}`.trim());
  }
  return (await res.json()) as TestTelemetry;
}

/**
 * Under a failure message: what did the services do during this test? Error spans / HTTP ≥ 400 spans and warning+
 * logs of the test's trace, with links to the trace and its logs. The dashboard receives telemetry with a batch delay,
 * so an empty answer right after the run is retried a few times.
 */
export function aspireFailureContext(test: () => TestNode): Child {
  const store = getStore();
  const links = testLinks(test);
  const enabled = computed(() => !!links() && store.$.telemetryAvailable() !== false);
  // refetch when the result changes (a new failing run), not on every test object update
  const fetchKey = computed(() => {
    const t = test();
    return enabled() ? `${t.projectId}|${t.id}|${t.traceId}|${t.lastRunAt}` : "";
  });
  const state = signal<Telemetry>({ status: "loading" });

  effect(() => {
    const key = fetchKey();
    if (!key) return;
    const t = untracked(test);
    const ac = new AbortController();
    let timer = 0;
    let tries = 0;
    onCleanup(() => {
      ac.abort();
      window.clearTimeout(timer);
    });
    state.set({ status: "loading" });
    const load = () =>
      fetchTelemetry(t, ac.signal).then(
        (data) => {
          if (ac.signal.aborted) return;
          state.set({ status: "ready", data });
          // telemetry arrives in the dashboard ~1–5 s after the test: retry an empty answer a few times
          const age = t.lastRunAt ? Date.now() - new Date(t.lastRunAt).getTime() : Infinity;
          if (!data.spans.length && !data.logs.length && tries++ < 3 && age < 30_000) timer = window.setTimeout(load, 2500);
        },
        (err: unknown) => {
          if (ac.signal.aborted) return;
          state.set({ status: "error", message: err instanceof Error ? err.message : String(err) });
        },
      );
    void load();
  });

  return () => {
    const l = links();
    if (!l) return null;
    return h(
      "div",
      { class: "aspire-context" },
      () => {
        if (!enabled()) return null;
        const s = state();
        if (s.status === "loading") return h("div", { class: "aspire-context-note" }, h("span", { class: "spinner" }), " Loading service telemetry…");
        if (s.status === "error") return h("div", { class: "aspire-context-note", title: s.message }, icon("info", { size: 14 }), " Service telemetry unavailable: ", s.message);
        return telemetryList(s.data);
      },
      h(
        "div",
        { class: "aspire-context-links" },
        h("a", { class: "btn btn-small btn-link", href: l.trace, target: "_blank", rel: "noreferrer" }, glyph("trace"), " Trace of this test"),
        h("a", { class: "btn btn-small btn-link", href: l.logs, target: "_blank", rel: "noreferrer" }, glyph("logs"), " Service logs"),
      ),
    );
  };
}

const MAX_ROWS = 6;

function telemetryList(data: TestTelemetry): Child {
  const spans = data.spans;
  const logs = data.logs;
  if (!spans.length && !logs.length)
    return h("div", { class: "aspire-context-note" }, icon("checkCircle", { size: 14 }), " No failed requests or warnings in this test's trace.");
  const all = signal(false);
  const rows = (): Child[] => {
    const out: Child[] = [...spans.map(spanRow), ...logs.map(logRow)];
    return all() ? out : out.slice(0, MAX_ROWS);
  };
  const total = spans.length + logs.length;
  return h(
    "div",
    { class: "aspire-context-body" },
    h("div", { class: "aspire-context-title" }, "Service activity during this test"),
    h("ul", { class: "aspire-context-list", "aria-label": "Error spans and logs of the test's trace" }, rows),
    total > MAX_ROWS
      ? h("button", { class: "raw-toggle", onclick: () => all.update((v) => !v) }, () => (all() ? "Show less" : `Show all ${total}`))
      : null,
  );
}

/** "5xx" / "4xx" for an HTTP error status, null otherwise. */
function httpErrorClass(code: number): string | null {
  if (code >= 500) return "5xx";
  return code >= 400 ? "4xx" : null;
}

function levelTag(level: TelemetryLog["level"]): string {
  if (level === "warning") return "warn";
  return level === "critical" ? "crit" : "error";
}

function spanRow(s: TelemetrySpan) {
  const detail = s.exceptionMessage || s.statusMessage;
  return h(
    "li",
    { class: "aspire-row span", title: s.exceptionType ? `${s.exceptionType}: ${s.exceptionMessage ?? ""}` : undefined },
    h("span", { class: "aspire-resource" }, s.resource),
    h("span", { class: "aspire-what" }, s.name),
    s.httpStatusCode ? h("span", { class: "aspire-http", "data-err": httpErrorClass(s.httpStatusCode) }, `→ ${s.httpStatusCode}`) : null,
    detail ? h("span", { class: "aspire-msg" }, `'${detail}'`) : null,
    s.durationMs != null ? h("span", { class: "aspire-dur" }, formatDuration(s.durationMs)) : null,
  );
}

function logRow(l: TelemetryLog) {
  return h(
    "li",
    { class: "aspire-row log", title: l.category },
    h("span", { class: "aspire-resource" }, l.resource),
    h("span", { class: `aspire-level lvl-${l.level}` }, levelTag(l.level)),
    h("span", { class: "aspire-msg" }, l.message),
  );
}
