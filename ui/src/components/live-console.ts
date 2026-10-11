/**
 * <lc-live-console>: live console output of the test shown in the detail pane (server event `testOutput`, see
 * ../live-output.ts).
 *
 *   - while the test runs: its output as it arrives, sticking to the bottom unless the user scrolled up ("Jump to
 *     latest" then shows how many lines arrived), a subtle "Live" indicator, "Follow running test" and "Show in Output"
 *     toggles. Lines are appended incrementally (no rebuild per chunk); the buffer is capped at 64 K characters.
 *   - after it finished: the final output (detail pane) is authoritative; this shows only the "live-only" lines, i.e.
 *     what was streamed but is not in the final output (e.g. test host console output the framework did not capture).
 *
 * a11y: the console is a focusable region, not a live region (no firehose); one polite status line when the output ends.
 */
import { computed, copyButton, cx, define, effect, h, icon, keyed, LabcoatElement, onCleanup, show, signal, untracked, type Child } from "@eriklieben/labcoat-design-system";
import type { LiveOutputSnapshot, TestNode } from "../contract";
import { getLiveOutput, residualLines, type LiveBuffer, type LiveLine } from "../live-output";
import { getStore, testKey } from "../store";
import { ansi } from "../util";
import "./live-console.css";

const isLive = (t: TestNode | undefined) => t?.outcome === "running" || t?.outcome === "queued";

/** Within `threshold` px of the bottom counts as "at the bottom" (keeps sticking). */
export function isNearBottom(m: { scrollTop: number; scrollHeight: number; clientHeight: number }, threshold = 24) {
  return m.scrollHeight - m.scrollTop - m.clientHeight < threshold;
}

/** Auto-scroll state: stick to the bottom until the user scrolls up; count lines that arrived meanwhile. */
export class AutoScroll {
  readonly stick = signal(true);
  readonly unseen = signal(0);

  /** The user (or we) scrolled: sticking again once back at the bottom. */
  onScroll(m: { scrollTop: number; scrollHeight: number; clientHeight: number }) {
    const near = isNearBottom(m);
    this.stick.set(near);
    if (near) this.unseen.set(0);
  }

  /** `added` lines were appended: true = scroll to the bottom now. */
  onAppend(added: number): boolean {
    if (this.stick.peek()) return true;
    if (added > 0) this.unseen.update((n) => n + added);
    return false;
  }

  /** "Jump to latest". */
  jump() {
    this.stick.set(true);
    this.unseen.set(0);
  }
}

function streamTag(l: LiveLine): string | null {
  if (l.shared) return "shared";
  if (l.stream === "stderr" || l.err) return "stderr";
  if (l.stream === "host") return "host";
  return null;
}

const STREAM_TITLE: Record<string, string> = {
  host: "Written by the test host process while this test ran (attributed by timing)",
  shared: "Written while several tests of this project were running: may belong to another one",
  stderr: "Standard error",
};

function lineEl(l: LiveLine, n: number): HTMLElement {
  const tag = streamTag(l);
  return h(
    "div",
    { class: cx("console-line", (l.stream === "stderr" || l.err) && "stderr", l.shared && "live-shared") },
    h("span", { class: "console-ln" }, String(n)),
    tag ? h("span", { class: cx("console-stream", `live-tag-${tag}`), title: STREAM_TITLE[tag] }, tag) : null,
    h("span", { class: "console-text" }, ansi(l.text)),
  );
}

function omittedEl(chars: number): HTMLElement {
  return h("div", { class: "console-line live-omitted" }, h("span", { class: "console-ln" }, "…"), h("span", { class: "console-text" }, `${chars.toLocaleString()} characters omitted (live view keeps the first 16 K and the last 48 K)`));
}

/** The status announced when the live output ends. */
function finishedMessage(lines: number): string {
  if (!lines) return "Test finished.";
  return lines === 1 ? "Test finished; 1 line of live output." : `Test finished; ${lines} lines of live output.`;
}

/** Renders a buffer into `el`, appending when possible. Returns the number of lines added. */
export class LineRenderer {
  private rev = -1;
  private count = 0;
  private omitted = 0;

  constructor(private readonly el: HTMLElement) {}

  render(b: LiveBuffer | undefined): number {
    const lines = b?.lines ?? [];
    if (b?.rev !== this.rev || lines.length < this.count || b.omitted !== this.omitted) {
      this.el.replaceChildren();
      const omitted = b?.omitted ?? 0;
      lines.forEach((l, i) => {
        if (omitted > 0 && i === b?.headLines) this.el.append(omittedEl(omitted));
        this.el.append(lineEl(l, i + 1));
      });
      const added = lines.length - (b?.rev === this.rev ? this.count : 0);
      this.rev = b?.rev ?? -1;
      this.omitted = b?.omitted ?? 0;
      this.count = lines.length;
      return Math.max(0, added);
    }
    // the last rendered line may have been continued (it had no newline yet)
    if (this.count > 0) {
      const lastEl = this.el.lastElementChild;
      if (lastEl) lastEl.replaceWith(lineEl(lines[this.count - 1], this.count));
    }
    for (let i = this.count; i < lines.length; i++) this.el.append(lineEl(lines[i], i + 1));
    const added = lines.length - this.count;
    this.count = lines.length;
    return added;
  }
}

export interface LiveConsoleProps {
  test: TestNode | undefined;
  /** "live": the output while the test runs (placed high in the detail pane); "residual": the live-only lines after it finished */
  kind: "live" | "residual";
}
export interface LiveConsole extends LiveConsoleProps {}

export class LiveConsole extends LabcoatElement<LiveConsoleProps> {
  static defaults: LiveConsoleProps = { test: undefined, kind: "live" };
  static hostClass = "live-console";

  protected render(): Child {
    this.style.display = "block";
    const live = getLiveOutput();
    const store = getStore();
    const test = this.props.test;
    const key = computed(() => (test() ? testKey(test()!) : undefined));
    const running = computed(() => isLive(test()));
    const status = signal("");

    effect(() => {
      const k = key();
      if (k) onCleanup(live.pin(k));
    });

    // a test that was already running when this page loaded: fetch what was streamed so far (once per key)
    const seeded = new Set<string>();
    effect(() => {
      const k = key();
      const t = untracked(test);
      if (!k || !t || !running() || seeded.has(k) || store.get().transport !== "ws" || untracked(this.props.kind) !== "live") return;
      seeded.add(k);
      if ((live.get(k)?.seq ?? 0) > 0) return;
      const q = new URLSearchParams({ projectId: t.projectId, id: t.id });
      fetch(new URL(`./api/tests/live-output?${q}`, location.href))
        .then((r) => (r.ok ? (r.json() as Promise<LiveOutputSnapshot>) : undefined))
        .then((snap) => snap && live.seed(k, snap))
        .catch(() => {});
    });

    // one polite summary when the live output ends (not per line)
    let wasRunning = untracked(running);
    effect(() => {
      const r = running();
      if (wasRunning && !r) {
        const n = untracked(() => live.get(key() ?? "")?.lines.length ?? 0);
        status.set(finishedMessage(n));
      } else if (r) status.set("");
      wasRunning = r;
    });

    const kind = untracked(this.props.kind);
    const view = keyed(
      () => `${running() ? "live" : "done"}|${key() ?? ""}`,
      (sig) => {
        if (sig.startsWith("live")) return kind === "live" ? this.liveView(key, live) : null;
        return kind === "residual" ? this.doneView(key, test, live) : null;
      },
    );

    return kind === "live" ? [view, h("span", { class: "visually-hidden", role: "status", "aria-live": "polite" }, status)] : view;
  }

  private liveView(key: () => string | undefined, live: ReturnType<typeof getLiveOutput>): Child {
    const scroll = new AutoScroll();
    const lineCount = signal(0);
    let consoleEl!: HTMLDivElement;
    let linesEl!: HTMLDivElement;
    let renderer: LineRenderer | undefined;
    const toBottom = () => {
      if (consoleEl) consoleEl.scrollTop = consoleEl.scrollHeight;
    };

    const header = h(
      "div",
      { class: "section-header" },
      h(
        "div",
        { class: "live-title" },
        h("h3", null, "Console output"),
        h("span", { class: "live-indicator", title: "Output appears while the test runs" }, h("span", { class: "live-dot lc-pulse", "aria-hidden": "true" }), "Live"),
      ),
      h(
        "span",
        { class: "section-extra live-actions" },
        h(
          "button",
          {
            class: () => cx("btn-link live-toggle", live.follow() && "on"),
            "aria-pressed": () => live.follow(),
            title: "Select each test as it starts running (stays on a test until it finishes)",
            onclick: () => live.setFollow(!live.follow.peek()),
          },
          icon("eye", { size: 14 }),
          " Follow running test",
        ),
        h(
          "button",
          {
            class: () => cx("btn-link live-toggle", live.mirror() && "on"),
            "aria-pressed": () => live.mirror(),
            title: "Also show live test output in the Output panel",
            onclick: () => live.setMirror(!live.mirror.peek()),
          },
          icon("console", { size: 14 }),
          " Show in Output",
        ),
        copyButton({ text: () => (live.get(key() ?? "")?.lines ?? []).map((l) => l.text).join("\n"), label: "Copy live output" }),
      ),
    );

    const body = h(
      "div",
      { class: "live-body" },
      h(
        "div",
        {
          class: "console live-scroll",
          tabIndex: 0,
          role: "region",
          "aria-label": "Live console output",
          ref: (el: HTMLDivElement) => (consoleEl = el),
          onscroll: () => scroll.onScroll(consoleEl),
        },
        () => (live.get(key() ?? "")?.gap ? h("div", { class: "console-empty" }, "Some output was missed (connection interrupted).") : null),
        h("div", { ref: (el: HTMLDivElement) => (linesEl = el) }),
        show(
          () => lineCount() === 0,
          () => h("div", { class: "console-empty" }, "Waiting for output…"),
        ),
      ),
      show(
        () => scroll.unseen() > 0,
        () =>
          h(
            "button",
            {
              class: "live-jump",
              onclick: () => {
                scroll.jump();
                toBottom();
              },
            },
            icon("chevronDown", { size: 14 }),
            () => ` Jump to latest (${scroll.unseen()} new)`,
          ),
      ),
    );

    // after the DOM above exists (refs assigned): append what arrived, keep the bottom in view while sticking
    effect(() => {
      live.tick();
      const b = live.get(key() ?? "");
      renderer ??= new LineRenderer(linesEl);
      const added = renderer.render(b);
      lineCount.set(b?.lines.length ?? 0);
      if (scroll.onAppend(added)) queueMicrotask(toBottom);
    });

    return h("section", { class: "detail-section live-section" }, header, body);
  }

  private doneView(key: () => string | undefined, test: () => TestNode | undefined, live: ReturnType<typeof getLiveOutput>): Child {
    const lines = computed(() => {
      live.tick();
      const t = test();
      return t ? residualLines(live.get(key() ?? ""), t) : [];
    });
    return show(
      () => lines().length > 0,
      () =>
        h(
          "section",
          { class: "detail-section live-section" },
          h(
            "div",
            { class: "section-header" },
            h("div", { class: "live-title" }, h("h3", null, "Live-only output")),
            h("span", { class: "section-extra" }, copyButton({ text: () => lines().map((l) => l.text).join("\n"), label: "Copy live-only output" })),
          ),
          h("p", { class: "detail-note live-note" }, "Streamed while the test ran but not part of its captured output (e.g. console output of the test host). Best effort; kept in this browser for the last tests."),
          () => h("div", { class: "console", tabIndex: 0, role: "region", "aria-label": "Live-only output" }, lines().map((l, i) => lineEl(l, i + 1))),
        ),
    );
  }
}

define("lc-live-console", LiveConsole);
declare global {
  interface HTMLElementTagNameMap {
    "lc-live-console": LiveConsole;
  }
}
