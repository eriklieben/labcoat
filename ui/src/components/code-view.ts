/**
 * <lc-code-view>: a read-only source view (port of CodeView.tsx): C# highlighting (highlight.js), line numbers, an
 * emphasised line range (the test method), error markers + inline annotations (failures), and an optional line
 * coverage overlay (hits + branch badges). Used by the detail pane (inline source) and the coverage views.
 *
 * Lines are rendered as one HTML string (cheap for files with thousands of lines) and rebuilt only when the text,
 * coverage overlay, annotations, markers or range change; focus changes only scroll.
 *
 * Also exports the shared helpers around source: `fetchSourceCached`, `highlightLines`, `parseFrames`, and the
 * "open in editor" helpers (Settings → editor, POST /api/open-in-editor, vscode:// fallback).
 *
 * Tests in the file (lead request "click a test in code → it is highlighted in the tree"): every line that declares a
 * known test method (TestNode.filePath + line; data-driven tests: their method) gets an IDE-like gutter button with the
 * test's current outcome, and its method name becomes a link.
 *   - click the gutter icon or the name → select that test in the tree (ancestors expanded, scrolled into view —
 *     smoothly when close — one locator ring) and show its details; focus moves to the tree;
 *   - the small ▶ next to the icon (hover / focus) runs only that test (data-driven: all its cases);
 *   - Ctrl/Cmd+click anywhere inside a test method's body (line..lineEnd) selects it (a plain click there does nothing:
 *     selecting text / placing the caret must stay possible);
 *   - keyboard: the gutter buttons are one tab stop inside the view (↑/↓ move between them, Enter = select,
 *     Shift+Enter = run). Screen readers: "Select test PlaceOrder_SendsConfirmationEmail (failed)".
 */
import hljs from "highlight.js/lib/core";
import csharp from "highlight.js/lib/languages/csharp";
import { animateEnter, computed, cx, define, effect, h, icon, LabcoatElement, locate, onCleanup, scrollToTop, signal, statusIcon, STATUS_LABEL, untracked, type Child, type Status } from "@eriklieben/labcoat-design-system";
import type { CoverageLineHit, FileCoverage, Outcome, TestNode } from "../contract";
import { treeKeyOf } from "../tree";
import { requestSelectNode } from "./coverage-line-tests";
import { focusTree, prepareTreeLanding } from "./tree-pane";
import { settings, type EditorId } from "../settings";
import { getStore } from "../store";
import { editorLink, fileName, trimTrailingSlashes } from "../util";
import "./code-view.css";

hljs.registerLanguage("csharp", csharp);

// ---- source fetching (tiny short-lived cache so selection/tab switches don't refetch) ---------------

const cache = new Map<string, { at: number; p: Promise<string> }>();
const TTL = 15_000;

export function fetchSourceCached(path: string, fetchSource: (p: string) => Promise<string>): Promise<string> {
  let entry = cache.get(path);
  if (!entry || Date.now() - entry.at > TTL) {
    entry = { at: Date.now(), p: fetchSource(path) };
    cache.set(path, entry);
    entry.p.catch(() => cache.delete(path));
  }
  return entry.p;
}

const errorStatus = (e: unknown) =>
  (e as { status?: number })?.status ?? (Number(/^(\d{3})\b/.exec(String((e as Error)?.message ?? e))?.[1]) || undefined);

// ---- highlighting ------------------------------------------------------------------------------------

const escapeHtml = (s: string) => s.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");


/** Highlight the whole file once, then split into lines keeping spans balanced across line breaks. */
export function highlightLines(text: string, path: string): string[] {
  const src = text.replace(/\r\n?/g, "\n").replace(/\n$/, "");
  const isCs = /\.(cs|csx)$/i.test(path);
  if (!isCs || src.length > 400_000) return src.split("\n").map(escapeHtml);
  const html = hljs.highlight(src, { language: "csharp", ignoreIllegals: true }).value;
  const out: string[] = [];
  const open: string[] = [];
  let cur = "";
  const re = /(<span[^>]*>)|(<\/span>)|(\n)|([^<\n]+|<)/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(html))) {
    if (m[1]) {
      cur += m[1];
      open.push(m[1]);
    } else if (m[2]) {
      cur += m[2];
      open.pop();
    } else if (m[3]) {
      out.push(cur + "</span>".repeat(open.length));
      cur = open.join("");
    } else cur += m[4];
  }
  out.push(cur + "</span>".repeat(open.length));
  return out;
}

// ---- stack frames --------------------------------------------------------------------------------------

export interface Frame {
  method: string;
  path: string;
  line: number;
}

// "   at Foo.Bar() in /path/File.cs:line 42", with the matches of /^\s*at\s+(.*?)\s+in\s+(.+?):line\s+(\d+)\s*$/ but
// without its super-linear backtracking: the line number at the end, then the head. The (shortest) method name never
// ends in whitespace; the path starts after the whitespace, or is its last character (one that `.` matches) when
// nothing follows it. Without a method name ("at  in …") the head has its own pattern.
const FRAME_TAIL_RE = /:line\s+(\d+)\s*$/;
const FRAME_HEAD_RE = /^\s*at\s+(\S(?:.*?\S)??)\s+in\s+(\S.*|(?=.)\s)$/;
const FRAME_HEAD_NO_METHOD_RE = /^\s*at\s{2,}in\s+(\S.*|(?=.)\s)$/;

/** One stack trace line as a frame with a source location, undefined for other lines. */
export function parseFrame(l: string): Frame | undefined {
  const tail = FRAME_TAIL_RE.exec(l);
  if (!tail) return undefined;
  const head = l.slice(0, tail.index);
  const m = FRAME_HEAD_RE.exec(head);
  if (m) return { method: m[1], path: m[2], line: Number(tail[1]) };
  const bare = FRAME_HEAD_NO_METHOD_RE.exec(head);
  return bare ? { method: "", path: bare[1], line: Number(tail[1]) } : undefined;
}

export function parseFrames(stack?: string): Frame[] {
  if (!stack) return [];
  const out: Frame[] = [];
  for (const l of stack.split(/\r?\n/)) {
    const f = parseFrame(l);
    if (f) out.push(f);
  }
  return out;
}

export const normPath = (p: string) => p.replaceAll("\\", "/").toLowerCase();
export const samePath = (a: string, b: string) => normPath(a) === normPath(b);

/**
 * Deterministic-build paths ("/_/src/Foo.cs", "/_1/…") are not real files: never link them. Everything else under one
 * of the served source roots (or, without roots, anything that is not a pathmap path) counts as "my code".
 */
export const isPathmapped = (p: string) => /^[\\/]_\d*[\\/]/.test(p);
export function isMyCode(path: string, roots: readonly string[] | undefined): boolean {
  if (isPathmapped(path)) return false;
  if (!roots?.length) return true;
  const p = normPath(path);
  return roots.some((r) => {
    const root = trimTrailingSlashes(normPath(r));
    return p === root || p.startsWith(`${root}/`);
  });
}

// ---- open in editor --------------------------------------------------------------------------------------

export type { EditorId };
const EDITOR_NAMES: Record<EditorId, string> = { vscode: "VS Code", rider: "Rider", visualstudio: "Visual Studio" };

/** The editor chosen in Settings (VS Code by default). Reactive when read in a binding. */
export function editorId(): EditorId {
  const e = settings().editor;
  return e === "rider" || e === "visualstudio" ? e : "vscode";
}
export const editorName = () => EDITOR_NAMES[editorId()];

/**
 * Open `path:line` in the chosen editor through the host (POST /api/open-in-editor launches it locally). VS Code
 * falls back to its vscode:// link when the host can't launch it (or in mock mode).
 */
export async function openInEditor(path: string, line?: number, column?: number) {
  const editor = editorId();
  const store = getStore();
  const fallback = () => {
    if (editor === "vscode") location.href = editorLink(path, line, column);
  };
  if (store.get().transport === "mock") return fallback();
  try {
    const res = await fetch(new URL("./api/open-in-editor", location.href), {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ editor, path, line, column }),
    });
    const body = (await res.json().catch(() => ({}))) as { ok?: boolean; message?: string };
    if (res.ok && body.ok !== false) return;
    console.warn("[open-in-editor]", res.status, body.message);
    fallback();
  } catch (err) {
    console.warn("[open-in-editor]", err);
    fallback();
  }
}

/**
 * An <a> that opens `path:line` in the chosen editor. Keeps the vscode:// href (copy link, middle click), but a plain
 * click goes through openInEditor(). `children` gets the editor's name when it is a function.
 */
export function editorAnchor(
  props: { class: string; path: () => string; line?: () => number | undefined; title?: (name: string) => string; "aria-label"?: (name: string) => string },
  ...children: Child[]
): HTMLAnchorElement {
  return h(
    "a",
    {
      class: props.class,
      href: () => editorLink(props.path(), props.line?.()),
      title: () => (props.title ? props.title(editorName()) : `Open in ${editorName()}`),
      "aria-label": props["aria-label"] ? () => props["aria-label"]!(editorName()) : undefined,
      onclick: (e: MouseEvent) => {
        if (e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        e.preventDefault();
        void openInEditor(props.path(), props.line?.());
      },
    },
    ...children,
  );
}

// ---- component ------------------------------------------------------------------------------------------

export interface Annotation {
  line: number;
  title: string;
  /** omitted: a one-line annotation (the failure box above already shows the message) */
  message?: string;
}

export interface CodeViewProps {
  /** fetched with the store's transport (GET /api/source), cached for a few seconds */
  path: string | undefined;
  /** inclusive line range to emphasise (e.g. the test method) */
  range: { from: number; to: number } | undefined;
  /** line to scroll into view; changes re-scroll */
  focusLine: number | undefined;
  /** bump to re-scroll to the same line */
  focusNonce: number | undefined;
  coverage: FileCoverage | undefined;
  showCoverage: boolean;
  annotations: Annotation[];
  /** lines with an error gutter marker (stack frames) */
  markers: number[];
  /** extra classes on the host, e.g. "inline" (the bordered box under a .code-head) */
  viewClass: string;
  /** coverage overlay: clicking a line's hit count (e.g. "which tests cover this line?"); unset = not clickable */
  onLineHits: ((line: number, anchor: HTMLElement) => void) | undefined;
}
export interface CodeView extends CodeViewProps {}

type Src = { text?: string; error?: string; status?: number; loading: boolean };

export class CodeView extends LabcoatElement<CodeViewProps> {
  static defaults: CodeViewProps = {
    path: undefined,
    range: undefined,
    focusLine: undefined,
    focusNonce: undefined,
    coverage: undefined,
    showCoverage: false,
    annotations: [],
    markers: [],
    viewClass: "",
    onLineHits: undefined,
  };

  protected render(): Child {
    const p = this.props;
    const src = signal<Src>({ loading: true });

    effect(() => {
      const path = p.path();
      const transport = getStore().transport;
      if (!path) return src.set({ loading: false, error: "No file" });
      let alive = true;
      onCleanup(() => (alive = false));
      src.set({ loading: true });
      fetchSourceCached(path, (x) => transport.fetchSource(x)).then(
        (text) => alive && src.set({ text, loading: false }),
        (e: unknown) => alive && src.set({ error: e instanceof Error ? e.message : String(e), status: errorStatus(e), loading: false }),
      );
    });

    const text = computed(() => src().text);
    const lines = computed(() => {
      const t = text();
      const path = untracked(p.path) ?? "";
      return t != null ? highlightLines(t, path) : [];
    });
    const withCov = computed(() => !!(p.coverage() && p.showCoverage()));
    const mode = computed(() => srcMode(src()));

    // host classes (the host is the React root div)
    effect(() => {
      const m = mode();
      this.className = cx("code-view", m === "ready" && "source-code", m === "error" && "code-unavailable", m === "ready" && withCov() && "with-cov", p.viewClass());
      if (m === "ready") {
        this.setAttribute("role", "table");
        this.setAttribute("aria-label", `Source of ${fileName(p.path() ?? "")}`);
        this.tabIndex = 0;
      } else {
        this.removeAttribute("role");
        this.removeAttribute("aria-label");
        this.removeAttribute("tabindex");
      }
    });

    // tests declared in this file: line → the test(s) (signature-gated: the html is rebuilt only when an outcome changes)
    const marksSig = computed(() => {
      const path = p.path();
      if (!path || mode() !== "ready") return "";
      return testMarksSignature(getStore().$.tests(), path);
    });
    const marks = computed(() => (marksSig() ? untracked(() => testMarks(getStore().get().tests, p.path()!)) : undefined));
    const html = computed(() => (mode() === "ready" ? buildHtml({ lines: lines(), coverage: p.coverage(), withCov: withCov(), annotations: p.annotations(), markers: p.markers(), range: p.range(), hitsClickable: !!p.onLineHits(), marks: marks() }) : ""));

    // Content: loading / unavailable states are small DOM; the ready state is one innerHTML write.
    const body = h("div", { class: "code-lines", style: "display: contents" });
    let lastHtml = "";
    let lastScrollKey = "";
    let lastFileKey = "";
    let lastNotes = "";
    effect(() => {
      const s = html();
      const target = p.focusLine() ?? p.range()?.from;
      const fileKey = `${lines().length}|${text()?.length}|${p.path()}`;
      const scrollKey = `${fileKey}|${target}|${p.focusNonce()}`;
      const notes = mode() === "ready" ? p.annotations().map((a) => `${a.line}:${a.title}`).join("|") : "";
      untracked(() => {
        if (s !== lastHtml) {
          // a focused gutter button survives the rebuild (an outcome changed): focus its replacement
          const a = document.activeElement as HTMLElement | null;
          const focusedTest = a && body.contains(a) ? a.dataset.test : undefined;
          body.innerHTML = s;
          lastHtml = s;
          rovingInit(body);
          if (focusedTest) body.querySelector<HTMLElement>(`.src-test-btn[data-test="${CSS.escape(focusedTest)}"]`)?.focus({ preventScroll: true });
        }
        // P7: a new failure annotation slides in from the left once the view has settled (120 ms delay)
        if (notes && notes !== lastNotes) for (const el of body.querySelectorAll<HTMLElement>(".src-annotation-box")) animateEnter(el, "x", { delay: 120 });
        lastNotes = notes;
        if (mode() !== "ready" || scrollKey === lastScrollKey) return;
        // same file: a navigation (stack frame, next uncovered, F8 within the file) → smooth for short distances + locator;
        // a new file: jump (it was just rendered)
        const navigation = fileKey === lastFileKey;
        lastScrollKey = scrollKey;
        lastFileKey = fileKey;
        requestAnimationFrame(() => {
          if (!target) {
            this.scrollTop = 0;
            return;
          }
          const row = body.querySelector<HTMLElement>(`[data-line="${target}"]`);
          if (!row) return;
          const top = Math.max(0, row.offsetTop - Math.min(this.clientHeight / 3, 120));
          if (navigation) {
            scrollToTop(this, top);
            locate(row);
          } else {
            this.scrollTop = top;
            if (row.classList.contains("err-line")) locate(row);
          }
        });
      });
    });

    const onClick = (e: MouseEvent) => {
      const target = e.target as HTMLElement;
      // tests in the code: gutter ▶ runs, gutter icon / name selects, Ctrl/Cmd+click in a method body selects
      const run = target.closest<HTMLElement>(".src-test-run");
      if (run?.dataset.test) return runMark(marks.peek(), run.dataset.test);
      const sel = target.closest<HTMLElement>(".src-test-btn, .src-test-name");
      if (sel?.dataset.test) return selectMark(marks.peek(), sel.dataset.test);
      if ((e.ctrlKey || e.metaKey) && !target.closest("a, button")) {
        const line = Number(target.closest<HTMLElement>("[data-line]")?.dataset.line);
        const m = line ? markAt(marks.peek(), line) : undefined;
        if (m) {
          e.preventDefault();
          return selectMark(marks.peek(), m.key);
        }
      }
      const cb = p.onLineHits.peek();
      const cell = target.closest<HTMLElement>(".src-hits-btn");
      if (!cb || !cell) return;
      const line = Number(cell.closest<HTMLElement>("[data-line]")?.dataset.line);
      if (line) cb(line, cell);
    };
    this.addEventListener("click", onClick);
    onCleanup(() => this.removeEventListener("click", onClick));
    // keyboard on the gutter buttons: ↑/↓ (Home/End) move, Enter selects, Shift+Enter runs
    const onKey = (e: KeyboardEvent) => {
      const btn = (e.target as HTMLElement).closest<HTMLElement>(".src-test-btn");
      if (!btn) return;
      const all = [...body.querySelectorAll<HTMLElement>(".src-test-btn")];
      const i = all.indexOf(btn);
      let j: number | undefined;
      if (e.key === "ArrowDown") j = Math.min(all.length - 1, i + 1);
      else if (e.key === "ArrowUp") j = Math.max(0, i - 1);
      else if (e.key === "Home") j = 0;
      else if (e.key === "End") j = all.length - 1;
      else if (e.key === "Enter") {
        e.preventDefault();
        if (e.shiftKey) runMark(marks.peek(), btn.dataset.test!);
        else selectMark(marks.peek(), btn.dataset.test!);
        return;
      } else return;
      e.preventDefault();
      for (const b of all) b.tabIndex = -1;
      all[j].tabIndex = 0;
      all[j].focus();
    };
    this.addEventListener("keydown", onKey);
    onCleanup(() => this.removeEventListener("keydown", onKey));

    const unavailable = () =>
      h(
        "div",
        { class: "empty-state small" },
        icon("document", { size: 28 }),
        h("div", { class: "empty-title" }, "Source not available"),
        h("div", { class: "empty-sub" }, unavailableReason(src())),
        isPathmapped(p.path() ?? "")
          ? null
          : editorAnchor({ class: "btn btn-small", path: () => p.path() ?? "", line: () => p.focusLine() }, icon("code"), () => ` Open in ${editorName()}`),
      );
    return [
      () => {
        const m = mode();
        if (m === "loading") return h("div", { class: "empty-state small" }, h("span", { class: "spinner" }), h("span", null, `Loading ${fileName(p.path() ?? "")}…`));
        return m === "error" ? unavailable() : null;
      },
      body,
    ];
  }
}
define("lc-code-view", CodeView);

declare global {
  interface HTMLElementTagNameMap {
    "lc-code-view": CodeView;
  }
}

function srcMode(s: Src): "loading" | "error" | "ready" {
  if (s.loading) return "loading";
  return s.error == null ? "ready" : "error";
}

function unavailableReason(s: Src): string {
  if (s.status === 403) return "The explorer host doesn't serve this file — it's outside the discovered projects and coverage report.";
  if (s.status === 404) return "The file no longer exists at this path.";
  return `Couldn't load the file (${s.error}).`;
}

// ---- HTML building ----------------------------------------------------------------------------------------

let markIcon = "";
let noteIcon = "";
const iconHtml = (size: number) => icon("dismissCircle", { size }).outerHTML;

function compact(n: number) {
  if (n >= 10000) return `${Math.round(n / 1000)}k`;
  return n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n);
}

/** `s` with the regular expression syntax characters escaped (matches `s` literally). */
const escapeRegExp = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, String.raw`\$&`);

interface HtmlInput {
  lines: string[];
  coverage: FileCoverage | undefined;
  withCov: boolean;
  annotations: Annotation[];
  markers: number[];
  range: { from: number; to: number } | undefined;
  hitsClickable: boolean;
  marks: TestMarks | undefined;
}

/** What a source row needs besides its line: the input indexed by line. */
interface RowContext extends HtmlInput {
  hits: Map<number, CoverageLineHit>;
  notes: Map<number, Annotation[]>;
  markerSet: Set<number>;
  /** line → the test whose method name is written on it */
  nameAt: Map<number, TestMark>;
}

function buildHtml(input: HtmlInput): string {
  markIcon ||= iconHtml(14);
  noteIcon ||= markIcon;
  const hits = new Map<number, CoverageLineHit>();
  if (input.coverage && input.withCov) for (const l of input.coverage.lines) hits.set(l.line, l);
  const notes = new Map<number, Annotation[]>();
  for (const a of input.annotations) notes.set(a.line, [...(notes.get(a.line) ?? []), a]);
  const ctx: RowContext = { ...input, hits, notes, markerSet: new Set(input.markers), nameAt: nameLines(input.lines, input.marks) };
  const out: string[] = [];
  for (let n = 1; n <= input.lines.length; n++) {
    out.push(rowHtml(ctx, n));
    for (const a of notes.get(n) ?? []) out.push(annotationHtml(a));
  }
  return out.join("");
}

/** The method name may sit a few lines below the reported line (attributes first): link it where it is written. */
function nameLines(lines: string[], marks: TestMarks | undefined): Map<number, TestMark> {
  const nameAt = new Map<number, TestMark>();
  if (!marks) return nameAt;
  for (const m of marks.byLine.values()) {
    const n = nameLine(lines, m);
    if (n !== undefined && !nameAt.has(n)) nameAt.set(n, m);
  }
  return nameAt;
}

function nameLine(lines: string[], m: TestMark): number | undefined {
  const re = new RegExp(String.raw`\b${escapeRegExp(m.name)}\b`);
  const last = Math.min(lines.length, Math.max(m.lineEnd, m.line + 6));
  for (let n = m.line; n <= last; n++) if (re.test(stripTags(lines[n - 1] ?? ""))) return n;
  return undefined;
}

/** A line hit by tests that took only some of its branches. */
const isPartial = (hit: CoverageLineHit | undefined) => !!(hit?.branchesTotal && (hit.branchesCovered ?? 0) < hit.branchesTotal && hit.hits > 0);

function coverageClass(hit: CoverageLineHit | undefined, partial: boolean): string {
  if (!hit) return "";
  if (hit.hits === 0) return "uncov";
  return partial ? "partial" : "cov";
}

/** "in-range" (+ "range-start" / "range-end") for a line of the emphasised range. */
function rangeClass(range: { from: number; to: number } | undefined, n: number): string | undefined {
  if (!range || n < range.from || n > range.to) return undefined;
  return cx("in-range", n === range.from && "range-start", n === range.to && "range-end");
}

function rowHtml(ctx: RowContext, n: number): string {
  const hit = ctx.hits.get(n);
  const partial = isPartial(hit);
  const marked = ctx.markerSet.has(n) || ctx.notes.has(n);
  const cls = cx("src-line", coverageClass(hit, partial), rangeClass(ctx.range, n), marked && "err-line");
  const tm = ctx.marks?.byLine.get(n);
  let row = `<div data-line="${n}" class="${cx(cls, tm && "has-test")}" role="row"><span class="src-mark" role="cell" aria-hidden="${!marked}">${marked ? markIcon : ""}</span>`;
  if (ctx.marks) row += `<span class="src-test" role="cell">${tm ? testGutter(tm) : ""}</span>`;
  row += `<span class="src-ln" role="cell">${n}</span>`;
  if (ctx.withCov) row += coverageCellsHtml(hit, partial, ctx.hitsClickable);
  const text = ctx.lines[n - 1] || " ";
  const nm = ctx.nameAt.get(n);
  return `${row}<span class="src-text hljs" role="cell">${nm ? linkName(text, nm) : text}</span></div>`;
}

function hitCountText(hit: CoverageLineHit | undefined): string {
  if (!hit) return "";
  return hit.hits === 0 ? "0" : `${compact(hit.hits)}×`;
}

function hitCountTitle(hit: CoverageLineHit | undefined): string {
  if (!hit) return "Not executable";
  return hit.hits === 1 ? "1 hit" : `${hit.hits} hits`;
}

/** The coverage overlay's cells: the hit count (a button when clickable) and the branch badge. */
function coverageCellsHtml(hit: CoverageLineHit | undefined, partial: boolean, hitsClickable: boolean): string {
  const hitText = hitCountText(hit);
  const hitTitle = hitCountTitle(hit);
  const hitsCell =
    hit && hitsClickable
      ? `<span class="src-hits" role="cell"><button type="button" class="src-hits-btn" title="${hitTitle} — which tests cover this line?">${hitText}</button></span>`
      : `<span class="src-hits" role="cell" title="${hitTitle}">${hitText}</span>`;
  return `${hitsCell}<span class="src-branch" role="cell">${branchBadgeHtml(hit, partial)}</span>`;
}

function branchBadgeHtml(hit: CoverageLineHit | undefined, partial: boolean): string {
  if (!hit?.branchesTotal) return "";
  const taken = hit.branchesCovered ?? 0;
  return `<span class="branch-badge ${branchClass(hit, partial)}" title="${taken} of ${hit.branchesTotal} branches taken">${taken}/${hit.branchesTotal}</span>`;
}

function branchClass(hit: CoverageLineHit, partial: boolean): string {
  if (partial) return "partial";
  return hit.hits === 0 || !hit.branchesCovered ? "uncov" : "cov";
}

function annotationHtml(a: Annotation): string {
  const message = a.message ? `<pre>${escapeHtml(a.message)}</pre>` : "";
  return `<div class="src-annotation${a.message ? "" : " short"}" role="row"><div class="src-annotation-box" role="cell"><div class="src-annotation-title">${noteIcon}${escapeHtml(a.title)}</div>${message}</div></div>`;
}

/**
 * `html` split into text and tag parts, like `html.split(/(<[^>]+>)/)` (tags at the odd indexes), in linear time: the
 * regex rescans the rest of the line from every "<" that has no ">" after it.
 */
export function splitTags(html: string): string[] {
  const parts: string[] = [];
  let text = 0;
  for (let i = html.indexOf("<"); i >= 0; ) {
    if (html[i + 1] === ">") {
      i = html.indexOf("<", i + 1); // "<>" is not a tag
      continue;
    }
    const close = html.indexOf(">", i + 1);
    if (close < 0) break; // no tag after this point
    parts.push(html.slice(text, i), html.slice(i, close + 1));
    text = close + 1;
    i = html.indexOf("<", text);
  }
  parts.push(html.slice(text));
  return parts;
}

/** `html` without its tags (the text parts of `splitTags`). */
export const stripTags = (html: string) => splitTags(html).filter((_, k) => k % 2 === 0).join("");



// ---- tests declared in the file ------------------------------------------------------------------------------------

interface TestMark {
  /** tree key: `t:` (a test) or `m:` (a data-driven method) */
  key: string;
  line: number;
  /** last line of the method (Ctrl/Cmd+click range), the declaration line when unknown */
  lineEnd: number;
  /** the method name as written in the code */
  name: string;
  status: Outcome;
  label: string;
  tests: TestNode[];
}
interface TestMarks {
  byLine: Map<number, TestMark>;
  byKey: Map<string, TestMark>;
}

const normCache = new WeakMap<TestNode, string>();
const normOf = (t: TestNode) => {
  let n = normCache.get(t);
  if (n === undefined) {
    n = t.filePath ? normPath(t.filePath) : "";
    normCache.set(t, n);
  }
  return n;
};

/** Cheap signature of the file's tests (line:status per test): the gutter re-renders only when it changes. */
function testMarksSignature(tests: Map<string, TestNode>, path: string): string {
  const p = normPath(path);
  let sig = "";
  for (const t of tests.values()) if (t.line && normOf(t) === p) sig += `${t.projectId}|${t.id}@${t.line}:${t.outcome};`;
  return sig;
}

/** Aggregate outcome of a data-driven method's cases (like the tree's). */
function aggregateOutcome(list: TestNode[]): Outcome {
  const has = (o: Outcome[]) => list.some((t) => o.includes(t.outcome));
  if (has(["running"])) return "running";
  if (has(["queued"])) return "queued";
  if (has(["failed", "timedOut"])) return "failed";
  if (has(["notRun", "cancelled"])) return "notRun";
  if (list.every((t) => t.outcome === "skipped")) return "skipped";
  return "passed";
}

/** The file's tests grouped by method (the cases of a data-driven method share one group). */
function testsByMethod(tests: Map<string, TestNode>, path: string): Map<string, TestNode[]> {
  const p = normPath(path);
  const groups = new Map<string, TestNode[]>();
  for (const t of tests.values()) {
    if (!t.line || normOf(t) !== p) continue;
    const k = `${t.projectId}\u0000${t.line}\u0000${t.methodName ?? t.displayName}`;
    const g = groups.get(k);
    if (g) g.push(t);
    else groups.set(k, [t]);
  }
  return groups;
}

/**
 * A display name without its argument list ("Add(1, 2)" → "Add"): `name.replace(/\s*\(.*$/, "")` in linear time, i.e.
 * from the first "(" after the last line break (`.` stops at one), with the whitespace before it.
 */
export function withoutArgs(name: string): string {
  const lastBreak = Math.max(name.lastIndexOf("\n"), name.lastIndexOf("\r"), name.lastIndexOf("\u2028"), name.lastIndexOf("\u2029"));
  const open = name.indexOf("(", lastBreak + 1);
  return open < 0 ? name : name.slice(0, open).trimEnd();
}

/** "failed" for a single test; "3 cases, 1 failed" / "3 cases, passed" for a data-driven method. */
function marksSummary(list: TestNode[], status: Outcome): string {
  const label = STATUS_LABEL[status].toLowerCase();
  if (list.length === 1) return label;
  const failed = list.filter((t) => t.outcome === "failed" || t.outcome === "timedOut").length;
  const detail = failed ? `${failed} failed` : label;
  return `${list.length} cases, ${detail}`;
}

function testMark(list: TestNode[]): TestMark {
  const t0 = list[0];
  const status = list.length === 1 ? t0.outcome : aggregateOutcome(list);
  const name = t0.methodName ?? withoutArgs(t0.displayName);
  const line = t0.line!;
  const lineEnd = Math.max(line, ...list.map((t) => t.lineEnd ?? 0));
  return { key: treeKeyOf(list), line, lineEnd, name, status, label: `${name} (${marksSummary(list, status)})`, tests: list };
}

function testMarks(tests: Map<string, TestNode>, path: string): TestMarks | undefined {
  const groups = testsByMethod(tests, path);
  if (!groups.size) return undefined;
  const byLine = new Map<number, TestMark>();
  const byKey = new Map<string, TestMark>();
  for (const list of groups.values()) {
    const m = testMark(list);
    if (!byLine.has(m.line)) byLine.set(m.line, m);
    byKey.set(m.key, m);
  }
  return { byLine, byKey };
}

const statusHtml = new Map<string, string>();
const runHtml = () => (statusHtml.get("__run") ?? statusHtml.set("__run", icon("play", { size: 12 }).outerHTML).get("__run")!);
function testGutter(m: TestMark): string {
  let ic = statusHtml.get(m.status);
  if (!ic) {
    ic = (statusIcon(m.status as Status, 14) as HTMLElement).outerHTML;
    statusHtml.set(m.status, ic);
  }
  const key = escapeHtml(m.key).replaceAll('"', "&quot;");
  const label = escapeHtml(m.label).replaceAll('"', "&quot;");
  return (
    `<button type="button" class="src-test-btn" data-test="${key}" tabindex="-1" aria-label="Select test ${label}" title="Select ${label} in the tree">${ic}</button>` +
    `<button type="button" class="src-test-run" data-test="${key}" tabindex="-1" aria-label="Run test ${label}" title="Run ${escapeHtml(m.name)}">${runHtml()}</button>`
  );
}

/** Wrap the first occurrence of the method name (outside tags) in a link-styled span. */
function linkName(html: string, m: TestMark): string {
  const re = new RegExp(String.raw`(^|[^\w])(${escapeRegExp(m.name)})(?![\w])`);
  let out = "";
  let done = false;
  for (const part of splitTags(html)) {
    if (done || part.startsWith("<")) {
      out += part;
      continue;
    }
    const x = re.exec(part);
    if (!x) {
      out += part;
      continue;
    }
    const at = x.index + x[1].length;
    out += `${part.slice(0, at)}<span class="src-test-name" data-test="${escapeHtml(m.key).replaceAll('"', "&quot;")}" title="Select ${escapeHtml(m.label).replaceAll('"', "&quot;")} in the tree (Ctrl/Cmd+click in the method body works too)">${x[2]}</span>${part.slice(at + x[2].length)}`;
    done = true;
  }
  return out;
}

/** The first gutter button is the view's one tab stop for them (roving). */
function rovingInit(body: HTMLElement) {
  const first = body.querySelector<HTMLElement>(".src-test-btn");
  if (first) first.tabIndex = 0;
}

function markAt(marks: TestMarks | undefined, line: number): TestMark | undefined {
  if (!marks) return undefined;
  for (const m of marks.byKey.values()) if (line >= m.line && line <= m.lineEnd) return m;
  return undefined;
}

/** Select the test in the tree (reveal + locator ring) and show it; keyboard focus goes to the tree. */
function selectMark(marks: TestMarks | undefined, key: string) {
  const m = marks?.byKey.get(key);
  if (!m) return;
  prepareTreeLanding(key);
  requestSelectNode(key, m.tests[0]);
  requestAnimationFrame(() => focusTree());
}

/** Run only this test (a data-driven method: all its cases). */
function runMark(marks: TestMarks | undefined, key: string) {
  const m = marks?.byKey.get(key);
  if (!m) return;
  const s = getStore();
  if (s.get().currentRun || s.get().conn !== "open") return;
  s.send({ type: "run", projectId: m.tests[0].projectId, testIds: m.tests.map((t) => t.id) });
}
