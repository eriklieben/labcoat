import { h, onCleanup as onCleanupUtil, type Child } from "@eriklieben/labcoat-design-system/core";
// util ⇄ store is an import cycle; fine: openInEditor is only called from event handlers (after both modules loaded)
import { getStore as getStoreLazy, openInEditor } from "./store";

export function formatDuration(ms?: number): string {
  if (ms == null || Number.isNaN(ms)) return "";
  if (ms < 1) return ms === 0 ? "0 ms" : "<1 ms";
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 2 : 1)} s`;
  const m = Math.floor(ms / 60_000);
  const s = Math.round((ms % 60_000) / 1000);
  return `${m}m ${s}s`;
}

export function formatClock(iso?: string): string {
  if (!iso) return "";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  return d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

export function formatRelative(iso?: string): string {
  if (!iso) return "";
  const d = new Date(iso).getTime();
  const diff = Date.now() - d;
  if (diff < 10_000) return "just now";
  if (diff < 60_000) return `${Math.round(diff / 1000)}s ago`;
  if (diff < 3_600_000) return `${Math.round(diff / 60_000)}m ago`;
  return formatClock(iso);
}

export function editorLink(path: string, line?: number, col?: number): string {
  let p = path.replaceAll("\\", "/");
  if (!p.startsWith("/")) p = "/" + p;
  return `vscode://file${encodeURI(p)}${lineColSuffix(line, col)}`;
}

/** ":line:col", ":line" or "" */
function lineColSuffix(line?: number, col?: number): string {
  if (!line) return "";
  return col ? `:${line}:${col}` : `:${line}`;
}

export function fileName(path: string) {
  const parts = path.split(/[\\/]/);
  return parts.at(-1)!;
}

export { cx } from "@eriklieben/labcoat-design-system/core";

// ---- file:line linkification ------------------------------------------------------------------

// The patterns below avoid two quantifiers that can trade the same characters (super-linear backtracking on long
// lines); each keeps the matches of the simpler pattern quoted in its comment. `(?=.)\s` is a whitespace character
// that `.` matches (not a line break).

// .NET stack frame:  "   at Foo.Bar() in /path/File.cs:line 42", as /^(\s*at\s+)(.*?)( in )(.+?):line (\d+)\s*$/:
// the line number at the end, then the head up to the first " in " that leaves a path.
const STACK_TAIL_RE = /:line (\d+)\s*$/;
const STACK_HEAD_RE = /^(\s*at\s+)(\S.*?|)( in )(?=.)/;
// MSBuild diagnostic: "/path/File.cs(42,37): error CS1002: ; expected", as
// /^(\s*)(.+?\.(?:cs|fs|vb|razor|csproj|props|targets))\((\d+),(\d+)\)(:.*)$/: the path starts after the indentation, or
// (a bare ".cs") takes its last whitespace character
const MSBUILD_RE = /^(\s*)((?:\S.*?|(?=.)\s)\.(?:cs|fs|vb|razor|csproj|props|targets))\((\d+),(\d+)\)(:.*)$/;
// any other frame ("   at System.Foo()"), as /^(\s*at\s+)(.*)$/
const EXTERNAL_FRAME_RE = /^(\s*at\s+)((?:\S.*)?)$/;
const LINE_BREAK_RE = /[\n\r\u2028\u2029]/;

/** The parts of a .NET stack frame line with a source location ("   at Foo.Bar() in /path/File.cs:line 42"). */
export function parseStackFrame(line: string): { at: string; method: string; inWord: string; path: string; line: string } | undefined {
  const tail = STACK_TAIL_RE.exec(line);
  if (!tail) return undefined;
  const head = line.slice(0, tail.index);
  const m = STACK_HEAD_RE.exec(head);
  if (!m) return undefined;
  const path = head.slice(m[0].length);
  if (LINE_BREAK_RE.test(path)) return undefined;
  return { at: m[1], method: m[2], inWord: m[3], path, line: tail[1] };
}

/** The parts of any other "at" frame line ("   at System.Foo()"): the "at" prefix and the rest. */
export function parseExternalFrame(line: string): { at: string; rest: string } | undefined {
  const m = EXTERNAL_FRAME_RE.exec(line);
  return m ? { at: m[1], rest: m[2] } : undefined;
}

export interface FrameLinkOptions {
  /** open the frame in the inline code view instead of VS Code */
  onOpenFrame?: (path: string, line: number) => void;
  /** `${path}:${line}` of the frame currently shown, to highlight it (reactive: pass an accessor) */
  active?: () => string | undefined;
}

const VSCODE_SVG =
  '<svg width="13" height="13" viewBox="0 0 20 20" aria-hidden="true"><path fill="currentColor" d="M7.2 5.1a.75.75 0 0 1 .03 1.06L3.53 10l3.7 3.84a.75.75 0 1 1-1.08 1.04l-4.2-4.36a.75.75 0 0 1 0-1.04l4.2-4.36a.75.75 0 0 1 1.05-.02m5.6 0a.75.75 0 0 1 1.05.02l4.2 4.36c.28.29.28.75 0 1.04l-4.2 4.36a.75.75 0 1 1-1.08-1.04L16.47 10l-3.7-3.84a.75.75 0 0 1 .03-1.06" /></svg>';

/** file:line links go through the editor setting (POST /api/open-in-editor; VS Code link as the fallback). */
function openViaSetting(e: MouseEvent, path: string, line?: number, col?: number) {
  if (e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey) return;
  e.preventDefault();
  void openInEditor(path, line, col);
}

/** One line of a stack trace / build output as a `.trace-line` element with file:line links. */
export function linkifyLine(line: string, opts?: FrameLinkOptions): HTMLDivElement {
  const frame = parseStackFrame(line);
  if (frame) {
    const { at, method, inWord, path, line: ln } = frame;
    const frameKey = `${path}:${ln}`;
    const lineNo = Number(ln);
    return h(
      "div",
      { class: "trace-line" },
      h("span", { class: "trace-dim" }, at),
      h("span", { class: "trace-method" }, method),
      h("span", { class: "trace-dim" }, inWord),
      opts?.onOpenFrame
        ? [
            h(
              "button",
              {
                class: () => `frame-btn${opts.active?.() === frameKey ? " active" : ""}`,
                onclick: () => opts.onOpenFrame!(path, lineNo),
                title: `Show ${frameKey}`,
              },
              `${fileName(path)}:${ln}`,
            ),
            h("a", {
              class: "frame-vscode",
              href: editorLink(path, lineNo),
              title: "Open in editor",
              "aria-label": "Open in editor",
              innerHTML: VSCODE_SVG,
              onclick: (e: MouseEvent) => openViaSetting(e, path, lineNo),
            }),
          ]
        : h(
            "a",
            { class: "trace-link", href: editorLink(path, lineNo), title: `Open ${frameKey} in the editor`, onclick: (e: MouseEvent) => openViaSetting(e, path, lineNo) },
            `${fileName(path)}:${ln}`,
          ),
    );
  }
  const m = MSBUILD_RE.exec(line);
  if (m) {
    const [, lead, path, ln, col, rest] = m;
    return h(
      "div",
      { class: "trace-line" },
      lead,
      h(
        "a",
        {
          class: "trace-link",
          href: editorLink(path, Number(ln), Number(col)),
          title: `Open ${path} in the editor`,
          onclick: (e: MouseEvent) => openViaSetting(e, path, Number(ln), Number(col)),
        },
        `${path}(${ln},${col})`,
      ),
      ansi(rest),
    );
  }
  const external = parseExternalFrame(line);
  if (external) {
    return h("div", { class: "trace-line trace-external" }, h("span", { class: "trace-dim" }, external.at), external.rest);
  }
  return h("div", { class: "trace-line" }, ansi(line));
}

// ---- minimal ANSI SGR renderer ------------------------------------------------------------------

const FG: Record<number, string> = {
  30: "black",
  31: "red",
  32: "green",
  33: "yellow",
  34: "blue",
  35: "magenta",
  36: "cyan",
  37: "white",
  90: "bright-black",
  91: "bright-red",
  92: "bright-green",
  93: "bright-yellow",
  94: "bright-blue",
  95: "bright-magenta",
  96: "bright-cyan",
  97: "bright-white",
};

// eslint-disable-next-line no-control-regex
const ANSI_RE = /\u001b\[([0-9;]*)m/g;

/** Text with ANSI SGR colors (foreground + bold) as text nodes / styled spans. */
export function ansi(text: string): Child {
  if (!text.includes("\u001b[")) return text;
  const out: Child[] = [];
  let last = 0;
  let fg: string | undefined;
  let bold = false;
  let m: RegExpExecArray | null;
  const push = (s: string) => {
    if (!s) return;
    out.push(fg || bold ? h("span", { style: { color: fg ? `var(--lc-ansi-${fg})` : undefined, fontWeight: bold ? 600 : undefined } }, s) : s);
  };
  ANSI_RE.lastIndex = 0;
  while ((m = ANSI_RE.exec(text))) {
    push(text.slice(last, m.index));
    last = ANSI_RE.lastIndex;
    const codes = m[1] === "" ? [0] : m[1].split(";").map(Number);
    for (const c of codes) {
      if (c === 0) {
        fg = undefined;
        bold = false;
      } else if (c === 1) bold = true;
      else if (c === 22) bold = false;
      else if (c === 39) fg = undefined;
      else if (FG[c]) fg = FG[c];
    }
  }
  push(text.slice(last));
  return out;
}

export function stripAnsi(s: string) {
  return s.replace(ANSI_RE, "");
}

// ---- persisted prefs ------------------------------------------------------------------------------

export function loadPref<T>(key: string, fallback: T): T {
  try {
    const v = localStorage.getItem(`mste.${key}`);
    return v == null ? fallback : (JSON.parse(v) as T);
  } catch {
    return fallback;
  }
}

export function savePref(key: string, value: unknown) {
  try {
    localStorage.setItem(`mste.${key}`, JSON.stringify(value));
  } catch {
    /* storage unavailable */
  }
}

/** Stable color per project, from the Aspire accent palette. */
const ACCENTS = ["teal", "royal-blue", "orchid", "peach", "seafoam", "marigold", "coral", "brand-blue", "jade", "berry"];
export function projectAccent(id: string) {
  // Java's String.hashCode over UTF-16 code units: the Int32Array slot wraps the sum to 32 bits on every step
  const h = new Int32Array(1);
  for (let i = 0; i < id.length; i++) h[0] = h[0] * 31 + id.charCodeAt(i);
  return `var(--lc-tag-${ACCENTS[Math.abs(h[0]) % ACCENTS.length]})`;
}

/** A run's time for the run switcher: "10:54:51 PM" today, "Yesterday 10:54 PM", else "Oct 6, 10:54 PM". */
export function formatRunTime(iso?: string): string {
  if (!iso) return "";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  const day = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diffDays = Math.round((day(new Date()) - day(d)) / 86_400_000);
  if (diffDays === 0) return formatClock(iso);
  const hm = d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  if (diffDays === 1) return `Yesterday ${hm}`;
  return `${d.toLocaleDateString(undefined, { month: "short", day: "numeric" })}, ${hm}`;
}

/** "3 passed, 1 failed" (run switcher, snapshot banner). */
export function runCounts(r: { passed: number; failed: number; skipped: number; notRun?: number; aborted?: boolean; cancelled?: boolean }) {
  const counts = [r.passed && `${r.passed} passed`, r.failed && `${r.failed} failed`, r.skipped && `${r.skipped} skipped`, r.notRun && `${r.notRun} not run`].filter(Boolean).join(", ") || "no results";
  return `${counts}${runEndNote(r)}`;
}

function runEndNote(r: { aborted?: boolean; cancelled?: boolean }): string {
  if (r.aborted) return " (aborted)";
  return r.cancelled ? " (cancelled)" : "";
}

/** Coverage rate tone (>=80 good, >=50 warn, else bad) and percentage label. */
export function rateTone(rate: number) {
  if (rate >= 0.8) return "good";
  return rate >= 0.5 ? "warn" : "bad";
}
export const pct = (rate: number) => `${(rate * 100).toFixed(rate === 1 ? 0 : 1)}%`;

// ---- focus restore (review 2, #6) -----------------------------------------------------------------------------

/** What identifies a control across a re-render: tag + label / text + the first class. */
function focusKey(el: HTMLElement): string {
  const label = el.getAttribute("aria-label") ?? el.getAttribute("title") ?? el.textContent?.trim().slice(0, 60) ?? "";
  return `${el.tagName}|${el.classList[0] ?? ""}|${el.id && !el.id.startsWith("lc-") ? el.id : ""}|${label}`;
}

/**
 * Keeps keyboard focus inside `root` when the focused control is re-rendered, removed or disabled (a keyed region
 * rebuilt it, a run disabled it): focus moves to the equivalent control (same tag, class and label) once it exists and
 * is enabled again, else to `fallback()`. Never when the user moved focus away on purpose (that blur leaves the control
 * connected and enabled). Call inside a component's render (the observer is owned).
 */
export function keepFocus(root: HTMLElement, fallback?: () => HTMLElement | null | undefined) {
  let lost: { el: HTMLElement; key: string } | undefined;
  let last: { el: HTMLElement; key: string } | undefined;
  const onIn = (e: FocusEvent) => {
    const el = e.target as HTMLElement;
    last = { el, key: focusKey(el) };
    lost = undefined;
  };
  const onOut = (e: FocusEvent) => {
    const el = e.target as HTMLElement;
    if (e.relatedTarget) {
      lost = undefined;
      return;
    }
    // removal / disabling blur the element with no new target: check after the DOM update
    queueMicrotask(() => {
      if (!el.isConnected || (el as HTMLButtonElement).disabled) lost = last?.el === el ? last : { el, key: focusKey(el) };
      else if (last?.el === el) last = undefined; // the user moved focus away (clicked outside a control)
    });
  };
  const gone = (el: HTMLElement) => !el.isConnected || (el as HTMLButtonElement).disabled;
  const restore = () => {
    // some browsers don't blur an element that is removed or disabled: detect it here
    const a0 = document.activeElement;
    if (!lost && last && gone(last.el) && (!a0 || a0 === document.body)) lost = last;
    if (!lost) return;
    const a = document.activeElement;
    if (a && a !== document.body && a !== document.documentElement) {
      lost = undefined;
      return;
    }
    if (document.querySelector("dialog[open]")) return;
    const { el, key } = lost;
    if (el.isConnected && !(el as HTMLButtonElement).disabled && el.offsetParent !== null) {
      el.focus({ preventScroll: true });
      lost = undefined;
      return;
    }
    if (el.isConnected && (el as HTMLButtonElement).disabled) return; // wait until it is enabled again
    const same = [...root.querySelectorAll<HTMLElement>(el.tagName)].find((x) => !(x as HTMLButtonElement).disabled && x.offsetParent !== null && focusKey(x) === key);
    const target = same ?? fallback?.();
    lost = undefined;
    target?.focus({ preventScroll: true });
  };
  root.addEventListener("focusin", onIn);
  root.addEventListener("focusout", onOut);
  const mo = new MutationObserver(() => queueMicrotask(restore));
  mo.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ["disabled"] });
  onCleanupUtil(() => {
    root.removeEventListener("focusin", onIn);
    root.removeEventListener("focusout", onOut);
    mo.disconnect();
  });
}

// ---- paths ---------------------------------------------------------------------------------------------------------

/** `s` without trailing slashes ("a/b//" -> "a/b"); a linear scan instead of a backtracking `/\/+$/`. */
export function trimTrailingSlashes(s: string): string {
  let end = s.length;
  while (end > 0 && s[end - 1] === "/") end--;
  return end === s.length ? s : s.slice(0, end);
}

/**
 * A source path for display: relative to the served source root that contains it, keeping the root's own folder name
 * ("AspireSample.Tests/ApiServiceTests.cs"); else the path as given.
 */
export function displayPath(path: string, roots: readonly string[] | undefined = getStoreForPaths()?.get().sourceRoots): string {
  const norm = (p: string) => p.replaceAll("\\", "/");
  const p = norm(path);
  let best: string | undefined;
  for (const r0 of roots ?? []) {
    const r = trimTrailingSlashes(norm(r0));
    if ((p.startsWith(`${r}/`) || p.toLowerCase().startsWith(`${r.toLowerCase()}/`)) && (!best || r.length > best.length)) best = r;
  }
  if (!best) return path;
  const parent = best.slice(0, best.lastIndexOf("/") + 1);
  return p.slice(parent.length);
}

function getStoreForPaths() {
  try {
    return getStoreLazy();
  } catch {
    return undefined;
  }
}

/**
 * A path as `dir/` (dimmed, truncated from the LEFT when space runs out: the file name stays) + `file` + `:line`.
 * Markup: `span.path > span.path-dir + span.path-file + span.path-line`.
 */
export function pathView(path: string, line?: number, opts: { relative?: boolean } = {}): HTMLElement {
  const shown = opts.relative === false ? path : displayPath(path);
  const slash = Math.max(shown.lastIndexOf("/"), shown.lastIndexOf("\\"));
  return h(
    "span",
    { class: "path", title: line ? `${path}:${line}` : path },
    // LRM marks keep the slashes on the right side inside the rtl (left-ellipsis) box
    slash >= 0 ? h("span", { class: "path-dir" }, `‎${shown.slice(0, slash + 1)}‎`) : null,
    h("span", { class: "path-file" }, shown.slice(slash + 1)),
    line ? h("span", { class: "path-line" }, `:${line}`) : null,
  );
}
