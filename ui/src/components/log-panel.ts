/**
 * Output (port of LogPanel.tsx):
 *
 *   <lc-log-panel>  the output panel: project status chips, project / level filters, wrap, clear, hide; the console
 *                   sticks to the bottom unless the user scrolled up. The panel height (splitter) and its open state
 *                   live in the shell.
 *   <lc-log-strip>  the one-line status strip shown while the panel is closed: the last log line (click to open).
 *
 * Both read the logs and projects from the store.
 */
import { computed, cx, define, effect, h, icon, LabcoatElement, list, onCleanup, show, signal, untracked, type Child } from "@eriklieben/labcoat-design-system";
import type { TestProject } from "../contract";
import { getStore, type LogEntry } from "../store";
import { ansi, formatClock, linkifyLine, loadPref, projectAccent, savePref, stripAnsi } from "../util";
import { outputErrorCount } from "./build-errors";
import "./log-panel.css";

type LevelFilter = "" | "warning" | "error";

const projectState = (p: TestProject) => {
  if (p.status === "idle") return "Ready";
  return p.status === "error" ? "Build failed" : p.status[0].toUpperCase() + p.status.slice(1);
};

const LINK_RE = /\(\d+,\d+\):|:line \d+/;

// ---- <lc-log-panel> -------------------------------------------------------------------------------------------

export interface LogPanelProps {
  onClose: () => void;
}
export interface LogPanel extends LogPanelProps {}

export class LogPanel extends LabcoatElement<LogPanelProps> {
  static defaults: LogPanelProps = { onClose: () => {} };
  static hostClass = "log-panel";

  protected render(): Child {
    this.setAttribute("role", "region");
    this.setAttribute("aria-label", "Output");
    const store = getStore();
    const $ = store.$;
    const project = signal("");
    const level = signal<LevelFilter>("");
    const wrap = signal(loadPref("logWrap", true));
    effect(() => savePref("logWrap", wrap()));

    const filtered = computed(() => {
      const p = project();
      const l = level();
      const logs = $.logs();
      if (!p && !l) return logs;
      return logs.filter((x) => (!p || x.projectId === p) && (!l || (l === "error" ? x.level === "error" : x.level !== "info")));
    });

    // Stick to the bottom unless the user scrolled up.
    let scroller!: HTMLDivElement;
    let stick = true;
    const toBottom = () => {
      if (stick && scroller) scroller.scrollTop = scroller.scrollHeight;
    };
    effect(() => {
      filtered();
      wrap();
      // after the list effect (created later in render) has updated the DOM
      untracked(() => queueMicrotask(toBottom));
    });

    const line = (l: () => LogEntry, i: () => number) => {
      const e = l();
      return h(
        "div",
        { class: cx("console-line", e.level === "error" && "stderr", e.level === "warning" && "warn") },
        h("span", { class: "console-ln" }, () => i() + 1),
        h("span", { class: "console-ts" }, formatClock(e.timestamp)),
        // the project's accent pulled toward the text color (≥ 4.5:1 on every console background, also stderr tints)
        e.projectId ? h("span", { class: "console-src", style: { color: `color-mix(in srgb, ${projectAccent(e.projectId)} 35%, var(--lc-fg))` } }, e.projectId) : null,
        h("span", { class: "console-text" }, LINK_RE.test(e.message) ? linkifyLine(e.message) : ansi(e.message)),
      );
    };

    const toolbar = h(
      "div",
      { class: "log-toolbar" },
      h("div", { class: "log-title" }, icon("console", { size: 16 }), h("span", null, "Output"), h("span", { class: "log-count" }, () => filtered().length)),
      h(
        "div",
        { class: "project-status-strip" },
        list(
          $.projects,
          (p) => p.id,
          (p) =>
            h(
              "span",
              { class: () => cx("proj-status", `ps-${p().status}`), title: () => p().lastError || p().status },
              h("span", { class: "proj-dot", style: { background: projectAccent(p.peek().id) } }),
              () => p().name,
              h("span", { class: "proj-state" }, () => projectState(p())),
            ),
        ),
        show(
          () => $.projects().length === 0,
          () => h("span", { class: "dim" }, "No projects"),
        ),
      ),
      h(
        "div",
        { class: "log-actions" },
        h(
          "select",
          {
            class: "select",
            "aria-label": "Filter by project",
            onchange: (e) => project.set((e.currentTarget as HTMLSelectElement).value),
          },
          h("option", { value: "" }, "All sources"),
          list(
            $.projects,
            (p) => p.id,
            (p) => h("option", { value: p.peek().id, selected: () => project() === p().id }, () => p().name),
          ),
        ),
        h(
          "select",
          { class: "select", "aria-label": "Filter by level", onchange: (e) => level.set((e.currentTarget as HTMLSelectElement).value as LevelFilter) },
          h("option", { value: "" }, "All levels"),
          h("option", { value: "warning" }, "Warnings + errors"),
          h("option", { value: "error" }, "Errors only"),
        ),
        h(
          "button",
          { class: () => cx("icon-btn", wrap() && "toggled"), title: "Wrap lines", "aria-label": "Wrap lines", "aria-pressed": wrap, onclick: () => wrap.update((v) => !v) },
          icon("textWrap", { size: 18 }),
        ),
        h("button", { class: "icon-btn", title: "Clear output", "aria-label": "Clear output", onclick: () => store.clearLogs() }, icon("del", { size: 18 })),
        h(
          "button",
          { class: "icon-btn", title: "Hide output panel (Ctrl+`)", "aria-label": "Hide output panel", onclick: () => this.onClose() },
          icon("chevronDown", { size: 18 }),
        ),
      ),
    );

    const consoleEl = h(
      "div",
      {
        class: () => cx("console log-console", !wrap() && "nowrap"),
        tabIndex: 0,
        role: "log",
        "aria-label": "Output log",
        ref: (el: HTMLDivElement) => (scroller = el),
        onscroll: () => {
          stick = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 24;
        },
      },
      show(
        () => filtered().length === 0,
        () => h("div", { class: "console-empty" }, "No output yet. Build and run output will stream here."),
      ),
      list(filtered, (l) => l.seq, line),
    );

    // Keep the bottom in view when the panel is resized (splitter).
    const ro = new ResizeObserver(toBottom);
    ro.observe(consoleEl);
    onCleanup(() => ro.disconnect());

    return [toolbar, consoleEl];
  }
}
define("lc-log-panel", LogPanel);

// ---- <lc-log-strip> -------------------------------------------------------------------------------------------

export interface LogStripProps {
  /** open the output panel */
  onOpen: () => void;
}
export interface LogStrip extends LogStripProps {}

/** One-line status strip while the output panel is closed: the latest log line; click (or Enter) opens the panel. */
export class LogStrip extends LabcoatElement<LogStripProps> {
  static defaults: LogStripProps = { onOpen: () => {} };
  static hostClass = "log-strip";

  protected render(): Child {
    const $ = getStore().$;
    const last = computed(() => {
      const logs = $.logs();
      return logs.length ? logs.at(-1) : undefined;
    });
    const busy = computed(() => $.projects().some((p) => p.status === "building" || p.status === "discovering" || p.status === "running"));
    return h(
      "button",
      {
        class: () => cx("log-strip-btn", last()?.level === "error" && "lvl-error", last()?.level === "warning" && "lvl-warn"),
        title: "Show the output panel (Ctrl+`)",
        // name from content (the visible text first, WCAG 2.5.3) + visually hidden error count and what it does
        onclick: () => this.onOpen(),
      },
      () => (busy() ? h("span", { class: "spinner log-strip-spinner" }) : icon("console", { size: 14 })),
      h("span", { class: "log-strip-label" }, "Output"),
      show(
        last,
        (l) => [
          h("span", { class: "log-strip-ts" }, () => formatClock(l().timestamp)),
          () => {
            const pid = l().projectId;
            // the project's accent, pulled toward the text color: ≥ 4.5:1 on the strip in all themes
            return pid ? h("span", { class: "log-strip-src", style: { color: `color-mix(in srgb, ${projectAccent(pid)} 35%, var(--lc-fg))` } }, pid) : null;
          },
          h("span", { class: "log-strip-msg" }, () => firstLine(stripAnsi(l().message))),
        ],
        () => h("span", { class: "log-strip-msg dim" }, "No output yet"),
      ),
      show(
        () => outputErrorCount() > 0,
        () => [
          h("span", { class: "log-strip-errors", "aria-hidden": "true" }, icon("errorCircle", { size: 12 }), () => ` ${outputErrorCount() > 99 ? "99+" : outputErrorCount()}`),
          h("span", { class: "visually-hidden" }, () => `, ${outputErrorCount()} error${outputErrorCount() === 1 ? "" : "s"}`),
        ],
      ),
      h("span", { class: "visually-hidden" }, " — show the output panel"),
      icon("chevronUp", { size: 14, class: "log-strip-chevron" }),
    );
  }
}
define("lc-log-strip", LogStrip);

function firstLine(s: string) {
  const i = s.indexOf("\n");
  return i < 0 ? s : s.slice(0, i) + " …";
}

declare global {
  interface HTMLElementTagNameMap {
    "lc-log-panel": LogPanel;
    "lc-log-strip": LogStrip;
  }
}
