/**
 * <lc-run-affected>: the toolbar's "Run affected" split button (docs/contract.md "Run affected tests (git)").
 *
 *   [⑂ Run affected  main · 3] [▾]
 *
 * - The main part runs the tests affected by the changes against the last-used base (remembered per browser):
 *   uncommitted changes (`working`), the branch vs its base (`branch`, e.g. origin/main) or a commit range (`range`).
 * - The chevron opens a menu to pick the base; picking one opens the preview popover: the changed files (status, how
 *   each maps to tests, the executed methods it touches) and the affected tests grouped by project with their reasons
 *   ("Orders.cs changed (method Validate)"), then Run. It fetches `GET /api/affected` (never runs anything) and sends
 *   `{ type: "runAffected", base, ref?, coverage?, buildImpactMap? }`.
 * - The git state (branch, number of changed files) is shown subtly on the button, from `GET /api/git` (fetched on
 *   mount, on window focus and after each run).
 *
 * Module-level API for the shell / palette / shortcuts: `runAffected()`, `openAffectedPreview()`, `affectedBase`,
 * `gitInfo`, `baseLabel()`, `affectedRunLabel()`.
 */
import {
  computed,
  cx,
  define,
  effect,
  h,
  icon,
  isMac,
  LabcoatElement,
  list,
  menuButton,
  onCleanup,
  popover,
  show,
  signal,
  untracked,
  type Child,
  type MenuEntry,
} from "@eriklieben/labcoat-design-system";
import type { AffectedBase, AffectedFile, AffectedPlan, GitInfo, RunSummary } from "../contract";
import { getStore, serverEventListeners } from "../store";
import { fileName, loadPref, savePref } from "../util";
import "./run-affected.css";

// ---- base / ref --------------------------------------------------------------------------------------------------

const BASES: readonly AffectedBase[] = ["working", "branch", "range"];
const isBase = (v: unknown): v is AffectedBase => typeof v === "string" && (BASES as readonly string[]).includes(v);

/** Same rule as the host: refs / ranges of `[A-Za-z0-9._/@{}~^:+-]`, not starting with "-". */
export const REF_RE = /^[A-Za-z0-9._/@{}~^:+-]+$/;
export const validRef = (ref: string | undefined): ref is string => !!ref && REF_RE.test(ref) && !ref.startsWith("-") && ref.length <= 200;

/** The last-used base (per browser). */
const savedBase = loadPref<unknown>("affectedBase", "working");
export const affectedBase = signal<AffectedBase>(isBase(savedBase) ? savedBase : "working");
/** The last commit range (base "range"). */
const savedRef = loadPref<unknown>("affectedRef", "");
export const affectedRef = signal<string>(typeof savedRef === "string" ? savedRef : "");
/** The preview popover is open (the palette and Mod+Shift+Enter open it). */
export const affectedPreviewOpen = signal(false);
/** GET /api/git (undefined until the first answer). */
export const gitInfo = signal<GitInfo | undefined>(undefined);

export function setAffectedBase(base: AffectedBase, ref?: string) {
  affectedBase.set(base);
  savePref("affectedBase", base);
  if (ref !== undefined) {
    affectedRef.set(ref);
    savePref("affectedRef", ref);
  }
}

/** The base ref "Branch vs …" compares with. */
/** "main" until GET /api/git answered; "base" when the repository has no default base (no remote, no main/master). */
export const branchBaseName = (git: GitInfo | undefined) => git?.defaultBase ?? (git?.available ? "base" : "main");
/** The repository has no base for "Branch vs …" (GET /api/affected?base=branch would answer 404 noBase). */
export const noBranchBase = (git: GitInfo | undefined) => !!git?.available && !git.defaultBase;

/** "Uncommitted changes" / "Branch vs origin/main" / "Commit range main..HEAD". */
export function baseLabel(base: AffectedBase, git: GitInfo | undefined = gitInfo.peek(), ref?: string): string {
  if (base === "working") return "Uncommitted changes";
  if (base === "branch") return `Branch vs ${ref || branchBaseName(git)}`;
  return ref ? `Commit range ${ref}` : "Commit range…";
}

/** "Affected (uncommitted changes)" for a Run affected run (RunSummary.affected.strategy "git"), else undefined. */
export function affectedRunLabel(run: RunSummary | undefined): string | undefined {
  const a = run?.affected;
  if (a?.strategy !== "git") return undefined;
  return `Affected (${affectedRunWhat(a.base ?? "working", a.ref)})`;
}

function affectedRunWhat(base: AffectedBase, ref: string | undefined): string {
  if (base === "working") return "uncommitted changes";
  if (base === "branch") return `branch vs ${ref || "base"}`;
  return ref ? `range ${ref}` : "commit range";
}

// ---- REST --------------------------------------------------------------------------------------------------------

/** A failed GET /api/affected (or /api/git): HTTP status + the host's `code` ("notGitRepo", "unknownRef", …). */
export class AffectedError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code?: string,
  ) {
    super(message);
  }
}

type AffectedTransport = {
  fetchGitInfo?: (signal?: AbortSignal) => Promise<GitInfo>;
  fetchAffectedPlan?: (base: AffectedBase, ref: string | undefined, signal?: AbortSignal) => Promise<AffectedPlan>;
};

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(new URL(path, location.href), { signal });
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { error?: string; code?: string };
    throw new AffectedError(body.error || `${res.status} ${res.statusText || ""}`.trim(), res.status, body.code);
  }
  return (await res.json()) as T;
}

export async function fetchGitInfo(signal?: AbortSignal): Promise<GitInfo> {
  const store = getStore();
  const t = store.transport as unknown as AffectedTransport;
  if (t.fetchGitInfo) return t.fetchGitInfo(signal);
  if (store.get().transport === "mock") return (await import("../mock/affected")).mockGitInfo();
  return getJson<GitInfo>("./api/git", signal);
}

export async function fetchAffectedPlan(base: AffectedBase, ref: string | undefined, signal?: AbortSignal): Promise<AffectedPlan> {
  const store = getStore();
  const t = store.transport as unknown as AffectedTransport;
  if (t.fetchAffectedPlan) return t.fetchAffectedPlan(base, ref, signal);
  if (store.get().transport === "mock") return (await import("../mock/affected")).mockAffectedPlan(base, ref, [...store.get().tests.values()], signal);
  const q = new URLSearchParams({ base });
  if (ref) q.set("ref", ref);
  return getJson<AffectedPlan>(`./api/affected?${q}`, signal);
}

/**
 * Sets `gitInfo` only when the value changed: an equal answer (window focus, after every run) must not re-render
 * what depends on it, e.g. the open base menu (its focused item would be replaced and Enter lost).
 */
export function setGitInfo(g: GitInfo) {
  if (JSON.stringify(g) !== JSON.stringify(gitInfo.peek())) gitInfo.set(g);
}

let gitAbort: AbortController | undefined;
/** Re-reads GET /api/git into `gitInfo` (errors leave the last value; a network failure keeps the button usable). */
export async function refreshGit(): Promise<void> {
  gitAbort?.abort();
  const ac = (gitAbort = new AbortController());
  try {
    const g = await fetchGitInfo(ac.signal);
    if (!ac.signal.aborted) setGitInfo(g);
  } catch {
    /* host without the endpoint, offline: keep what we had */
  }
}

// ---- actions -----------------------------------------------------------------------------------------------------

export interface RunAffectedOptions {
  base?: AffectedBase;
  ref?: string;
  coverage?: boolean;
  buildImpactMap?: boolean;
}

/**
 * Sends runAffected (base default: the last-used one). A range without a valid ref opens the preview instead.
 * Returns false when nothing was sent.
 */
export function runAffected(o: RunAffectedOptions = {}): boolean {
  const base = o.base ?? affectedBase.peek();
  const ref = base === "range" ? (o.ref ?? affectedRef.peek()) : undefined;
  if (base === "range" && !validRef(ref)) {
    openAffectedPreview("range");
    return false;
  }
  setAffectedBase(base, base === "range" ? ref : undefined);
  getStore().send({
    type: "runAffected",
    base,
    ref,
    coverage: o.coverage ? true : undefined,
    buildImpactMap: o.buildImpactMap ? true : undefined,
  });
  return true;
}

/** Opens the preview popover (optionally switching the base first). */
export function openAffectedPreview(base?: AffectedBase) {
  if (base) setAffectedBase(base);
  affectedPreviewOpen.set(true);
}

/** Command palette entries (Run affected: uncommitted / branch, Preview…). */
export function affectedCommands(blocked: boolean): { id: string; label: string; detail?: string; icon: "branch" | "taskList"; shortcut?: string; disabled?: boolean; run: () => void }[] {
  const g = gitInfo.peek();
  const noGit = g?.available === false;
  const why = noGit ? "not a git repository" : undefined;
  return [
    {
      id: "affected-working",
      label: "Run affected: uncommitted changes",
      detail: why ?? (g?.dirty !== undefined ? plural(g.dirty, "changed file") : undefined),
      icon: "branch",
      disabled: blocked || noGit,
      run: () => {
        runAffected({ base: "working" });
      },
    },
    {
      id: "affected-branch",
      label: `Run affected: branch vs ${branchBaseName(g)}`,
      detail: why ?? (noBranchBase(g) ? "no base branch found (set affectedBase)" : g?.branch),
      icon: "branch",
      disabled: blocked || noGit || noBranchBase(g),
      run: () => {
        runAffected({ base: "branch" });
      },
    },
    { id: "affected-preview", label: "Preview affected tests…", detail: "changed files and the tests they affect", icon: "taskList", shortcut: "Mod+Shift+Enter", run: () => openAffectedPreview() },
  ];
}

// ---- display helpers ---------------------------------------------------------------------------------------------

const STATUS_LETTER: Record<AffectedFile["status"], string> = {
  added: "A",
  modified: "M",
  deleted: "D",
  renamed: "R",
  copied: "C",
  typeChanged: "T",
  untracked: "U",
  unmerged: "!",
};
const MAPPING_LABEL: Record<AffectedFile["mapping"], string> = {
  testFile: "test file",
  impactMap: "impact map",
  coverage: "cached coverage",
  project: "whole project",
  none: "no tests",
};
const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/** "12 tests in 2 projects · 3 files changed" (the preview headline, also announced). */
export function planHeadline(p: AffectedPlan): string {
  const files = `${plural(p.counts.files, "file")} changed`;
  if (p.counts.files === 0) return "No changed files";
  if (p.counts.tests === 0) return `No affected tests · ${files}`;
  return `${plural(p.counts.tests, "test")} in ${plural(p.counts.projects, "project")} · ${files}`;
}

/** "main · 3" (button chip: the branch and its number of uncommitted files); undefined when git is unavailable. */
export function gitSummary(g: GitInfo | undefined): string | undefined {
  if (!g?.available) return undefined;
  const where = g.branch ?? (g.head ? `${g.head} (detached)` : "no commits");
  return g.dirty ? `${where} · ${g.dirty}` : where;
}

type Load = { status: "idle" } | { status: "loading"; prev?: AffectedPlan } | { status: "ready"; plan: AffectedPlan } | { status: "error"; error: AffectedError | Error };

/** The plan to show: the answer, or while the next one loads the previous answer. */
function shownPlan(s: Load): AffectedPlan | undefined {
  if (s.status === "ready") return s.plan;
  return s.status === "loading" ? s.prev : undefined;
}

const dirtyText = (dirty: number | undefined) => (dirty ? plural(dirty, "changed file") : "working tree is clean");

function branchDescription(g: GitInfo | undefined): string | undefined {
  if (noBranchBase(g)) return "no base branch found (set affectedBase)";
  return g?.branch ? `${g.branch} since it left ${branchBaseName(g)}` : undefined;
}

const MAX_TESTS_SHOWN = 200;

// ---- element -----------------------------------------------------------------------------------------------------

/** No props: everything comes from the store and the module signals above. */
export type RunAffectedProps = Record<string, never>;
export class RunAffected extends LabcoatElement<RunAffectedProps> {
  static defaults: RunAffectedProps = {};

  protected render(): Child {
    this.style.display = "contents";
    const store = getStore();
    const $ = store.$;
    const running = computed(() => !!$.currentRun());
    const connected = computed(() => $.conn() === "open");

    let mainBtn!: HTMLButtonElement;
    // who opened the preview: focus goes back there when it closes
    let opener: HTMLElement | undefined;

    // git state: on mount, on focus, after each run
    void refreshGit();
    const onFocus = () => void refreshGit();
    window.addEventListener("focus", onFocus);
    onCleanup(() => window.removeEventListener("focus", onFocus));
    const onEvent = (e: { type: string }) => {
      if (e.type === "runFinished") void refreshGit();
    };
    serverEventListeners.add(onEvent);
    onCleanup(() => serverEventListeners.delete(onEvent));

    const gitText = computed(() => gitSummary(gitInfo()));
    const unavailable = computed(() => gitInfo()?.available === false);

    const mainTitle = () => {
      const g = gitInfo();
      if (g && !g.available) return g.error ? `Run affected needs a git repository: ${g.error}` : "Run affected needs a git repository";
      const base = affectedBase();
      let what: string;
      if (base === "working") what = "uncommitted changes";
      else if (base === "branch") what = `the changes on this branch vs ${branchBaseName(g)}`;
      else what = affectedRef() ? `the commits ${affectedRef()}` : "a commit range";
      return `Run the tests affected by ${what} (${isMac ? "⌘⇧↩" : "Ctrl+Shift+Enter"} to preview first)`;
    };

    const menuItems = (): MenuEntry[] => {
      const g = gitInfo();
      const base = affectedBase();
      return [
        { heading: "Compare with" },
        {
          label: "Uncommitted changes",
          type: "radio",
          checked: base === "working",
          description: g?.available ? dirtyText(g.dirty) : undefined,
          onSelect: () => openAffectedPreview("working"),
        },
        {
          label: `Branch vs ${branchBaseName(g)}`,
          text: "Branch",
          type: "radio",
          checked: base === "branch",
          description: branchDescription(g),
          onSelect: () => openAffectedPreview("branch"),
        },
        {
          label: "Commit range…",
          type: "radio",
          checked: base === "range",
          description: affectedRef() || "e.g. HEAD~3..HEAD",
          onSelect: () => openAffectedPreview("range"),
        },
        { separator: true },
        {
          label: "Preview affected tests…",
          icon: "taskList",
          shortcut: "Mod+Shift+Enter",
          onSelect: () => openAffectedPreview(),
        },
      ];
    };

    // Mod+Shift+Enter: open the preview (the global handler leaves Shift+Mod+Enter alone).
    const onKey = (e: KeyboardEvent) => {
      if (e.defaultPrevented || !(e.ctrlKey || e.metaKey) || !e.shiftKey || e.altKey || e.key !== "Enter") return;
      if (document.querySelector("dialog[open]")) return;
      e.preventDefault();
      opener = mainBtn;
      affectedPreviewOpen.set(true);
    };
    window.addEventListener("keydown", onKey);
    onCleanup(() => window.removeEventListener("keydown", onKey));

    onCleanup(() => affectedPreviewOpen.set(false));

    return h(
      "span",
      { class: "split-btn affected-split" },
      h(
        "button",
        {
          type: "button",
          class: "btn affected-btn",
          ref: (b) => (mainBtn = b),
          disabled: () => running() || !connected(),
          title: mainTitle,
          onclick: () => {
            opener = mainBtn;
            if (unavailable.peek()) affectedPreviewOpen.set(true);
            else runAffected();
          },
        },
        icon("branch"),
        " Run affected",
        show(gitText, (t) => [
          h("span", { class: "affected-git", "aria-hidden": "true" }, () => t()),
          h("span", { class: "visually-hidden" }, () => (t().includes(" · ") ? ` (${t().replace(" · ", ", ")} uncommitted)` : ` (${t()})`)),
        ]),
      ),
      (() => {
        const b = menuButton({
          label: "Run affected options",
          icon: "chevronDown",
          iconOnly: true,
          size: "small",
          class: "btn split-chevron",
          disabled: () => !connected(),
          items: menuItems,
        });
        // the menu closes (focus back on the chevron) before the preview opens
        b.addEventListener("focusin", () => (opener = b));
        return b;
      })(),
      popover({
        open: affectedPreviewOpen,
        anchor: () => (opener?.isConnected ? opener : mainBtn),
        onClose: () => affectedPreviewOpen.set(false),
        label: "Run affected tests",
        class: "affected-popover",
        placement: "bottom-start",
        initialFocus: (el) => el.querySelector<HTMLElement>(".ap-base input:checked") ?? el,
        content: () => h("lc-affected-preview", { onDone: () => affectedPreviewOpen.set(false) }),
      }),
    );
  }
}
define("lc-run-affected", RunAffected);

// ---- preview (the popover's content) -----------------------------------------------------------------------------

export interface AffectedPreviewProps {
  /** close the popover (after Run / Cancel) */
  onDone: () => void;
}
export interface AffectedPreview extends AffectedPreviewProps {}
export class AffectedPreview extends LabcoatElement<AffectedPreviewProps> {
  static defaults: AffectedPreviewProps = { onDone: () => {} };
  static hostClass = "affected-preview";

  readonly #coverage = signal(false);
  readonly #buildFirst = signal(false);
  readonly #refDraft = signal(affectedRef.peek());
  readonly #state = signal<Load>({ status: "idle" });
  readonly #tick = signal(0);

  protected render(): Child {
    const store = getStore();
    const $ = store.$;
    const running = computed(() => !!$.currentRun());
    const connected = computed(() => $.conn() === "open");
    this.setAttribute("role", "group");
    this.setAttribute("aria-labelledby", "ap-title");

    const refValid = computed(() => validRef(this.#refDraft().trim()));
    // what to fetch: base + ref (range) + impact map state (a finished pass narrows the plan) + manual refresh
    const im = computed(() => {
      const m = $.impactMap();
      if (!m) return "";
      return `${m.files}|${m.tests}|${m.building ? 1 : 0}|${m.builtAt ?? ""}`;
    });
    const building = computed(() => !!$.impactMap()?.building);
    const request = computed(() => {
      const base = affectedBase();
      const ref = base === "range" ? this.#refDraft().trim() : undefined;
      return { base, ref, key: `${base}|${ref ?? ""}|${im()}|${this.#tick()}` };
    });
    const requestKey = computed(() => request().key);

    let timer: number | undefined;
    let ac: AbortController | undefined;
    onCleanup(() => {
      window.clearTimeout(timer);
      ac?.abort();
    });
    effect(() => {
      requestKey();
      const { base, ref } = untracked(request);
      window.clearTimeout(timer);
      ac?.abort();
      if (base === "range" && !validRef(ref)) {
        this.#state.set({ status: "idle" });
        return;
      }
      const prev = untracked(this.#state);
      this.#state.set({ status: "loading", prev: shownPlan(prev) });
      const go = () => {
        const ctrl = (ac = new AbortController());
        fetchAffectedPlan(base, ref, ctrl.signal).then(
          (plan) => {
            if (ctrl.signal.aborted) return;
            this.#state.set({ status: "ready", plan });
            setGitInfo(plan.git);
          },
          (err: unknown) => {
            if (ctrl.signal.aborted || (err as Error)?.name === "AbortError") return;
            this.#state.set({ status: "error", error: err instanceof Error ? err : new Error(String(err)) });
          },
        );
      };
      // typing a range: wait for a pause; anything else fetches right away
      if (base === "range") timer = window.setTimeout(go, 350);
      else go();
    });

    const plan = computed(() => shownPlan(this.#state()));
    const headline = computed(() => {
      const s = this.#state();
      if (s.status === "idle") return affectedBase() === "range" ? "Enter a commit range, e.g. main..HEAD or HEAD~3..HEAD" : "";
      if (s.status === "error") return s.error.message;
      if (s.status === "loading" && !s.prev) return "Finding affected tests…";
      return planHeadline(plan()!);
    });
    const canRun = computed(() => {
      const s = this.#state();
      return s.status === "ready" && s.plan.counts.tests > 0 && !running() && connected();
    });

    const run = () => {
      const p = plan.peek();
      if (!canRun.peek() || !p) return;
      const base = affectedBase.peek();
      const sent = runAffected({
        base,
        ref: base === "range" ? this.#refDraft.peek().trim() : undefined,
        coverage: this.#coverage.peek(),
        buildImpactMap: this.#buildFirst.peek() && !!p.needsImpactMap,
      });
      if (sent) this.onDone();
    };

    const testName = (projectId: string, id: string) => store.get().tests.get(`${projectId}|${id}`)?.displayName ?? id;

    const baseRadio = (base: AffectedBase, label: () => string) =>
      h(
        "label",
        { class: () => cx("ap-base-opt", affectedBase() === base && "on") },
        h("input", {
          type: "radio",
          name: "ap-base",
          value: base,
          checked: () => affectedBase() === base,
          onchange: () => setAffectedBase(base),
        }),
        h("span", null, label),
      );

    return [
      h(
        "div",
        { class: "ap-head" },
        h("h2", { class: "ap-title", id: "ap-title" }, icon("branch"), " Run affected tests"),
        show(
          () => gitSummary(gitInfo()),
          (t) =>
            h(
              "span",
              { class: "ap-git", title: () => gitInfo()?.root ?? "" },
              () => t(),
              () => (gitInfo()?.dirty ? " uncommitted" : ""),
              () => {
                const g = gitInfo();
                return g?.ahead || g?.behind ? ` · ↑${g.ahead ?? 0} ↓${g.behind ?? 0}` : "";
              },
            ),
        ),
      ),
      h(
        "fieldset",
        { class: "ap-base" },
        h("legend", { class: "visually-hidden" }, "Compare with"),
        baseRadio("working", () => "Uncommitted changes"),
        baseRadio("branch", () => `Branch vs ${branchBaseName(gitInfo())}`),
        baseRadio("range", () => "Commit range"),
      ),
      show(
        () => affectedBase() === "range",
        () =>
          h(
            "div",
            { class: "ap-range" },
            h("label", { htmlFor: "ap-ref", class: "ap-range-label" }, "Range"),
            h("input", {
              id: "ap-ref",
              class: () => cx("ap-ref", this.#refDraft().trim() && !refValid() && "invalid"),
              type: "text",
              spellcheck: false,
              autocomplete: "off",
              placeholder: "main..HEAD",
              value: this.#refDraft.peek(),
              "aria-invalid": () => !!this.#refDraft().trim() && !refValid(),
              "aria-describedby": "ap-ref-hint",
              oninput: (e: Event) => this.#refDraft.set((e.currentTarget as HTMLInputElement).value),
              onkeydown: (e: KeyboardEvent) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  run();
                }
              },
            }),
            h("span", { id: "ap-ref-hint", class: "ap-hint" }, () =>
              this.#refDraft().trim() && !refValid() ? "Letters, digits and . _ / @ { } ~ ^ : + - only; must not start with -" : "A..B, A...B or one commit",
            ),
          ),
      ),
      h(
        "div",
        {
          class: () => cx("ap-status", this.#state().status === "error" && "error", this.#state().status === "loading" && "loading"),
          role: "status",
          "aria-live": "polite",
        },
        () => (this.#state().status === "error" ? icon("errorCircle") : null),
        h("span", { class: "ap-headline" }, headline),
        show(
          () => {
            const p = plan();
            return p && (p.mergeBase || p.ref) && p.base !== "working" ? p : undefined;
          },
          (p) => h("span", { class: "ap-ref-used" }, () => (p().mergeBase ? `merge-base ${p().mergeBase}` : (p().ref ?? ""))),
        ),
      ),
      show(
        () => plan()?.warnings?.length,
        () => h("ul", { class: "ap-warnings" }, () => (plan()?.warnings ?? []).map((w) => h("li", null, icon("warning"), " ", w))),
      ),
      show(
        () => plan()?.needsImpactMap,
        () =>
          h(
            "div",
            { class: "ap-impact" },
            h("span", null, "Some product files have no impact data, so every test of their project is selected."),
            h(
              "button",
              {
                type: "button",
                class: "btn btn-small",
                disabled: () => building() || running() || !connected(),
                onclick: () => store.send({ type: "buildImpactMap" }),
              },
              icon("targetArrow"),
              () => (building() ? " Building impact map…" : " Build impact map"),
            ),
          ),
      ),
      show(
        () => (plan()?.files.length ? plan() : undefined),
        (p) =>
          h(
            "details",
            { class: "ap-section", open: true },
            h("summary", null, () => `Changed files (${p().files.length})`),
            h(
              "ul",
              // scrollable: focusable so keyboard users can scroll it (axe scrollable-region-focusable)
              { class: "ap-files", tabIndex: 0, "aria-label": "Changed files" },
              list(
                () => p().files,
                (f) => f.path,
                (f) =>
                  h(
                    "li",
                    { class: () => cx("ap-file", f().mapping === "none" && "dim") },
                    h("span", { class: () => `ap-st st-${f().status}`, title: () => f().status, "aria-hidden": "true" }, () => STATUS_LETTER[f().status] ?? "?"),
                    h("span", { class: "visually-hidden" }, () => `${f().status}: `),
                    h(
                      "span",
                      { class: "ap-path", title: () => f().relativePath },
                      () => (f().oldPath ? `${f().oldPath} → ` : ""),
                      h("span", { class: "ap-dir" }, () => {
                        const r = f().relativePath;
                        return r.slice(0, r.length - fileName(r).length);
                      }),
                      h("span", { class: "ap-name" }, () => fileName(f().relativePath)),
                    ),
                    h(
                      "span",
                      { class: "ap-map" },
                      () => MAPPING_LABEL[f().mapping] ?? f().mapping,
                      () => (f().methods?.length ? ` · ${f().methods!.join(", ")}` : ""),
                    ),
                    h("span", { class: "ap-count" }, () => (f().tests ? plural(f().tests, "test") : "")),
                  ),
              ),
            ),
          ),
      ),
      show(
        () => (plan()?.tests.length ? plan() : undefined),
        (p) =>
          h(
            "details",
            { class: "ap-section", open: true },
            h("summary", null, () => `Affected tests (${p().counts.tests})`),
            h("div", { class: "ap-tests", tabIndex: 0, role: "group", "aria-label": "Affected tests" }, () => {
              const pl = p();
              let shown = 0;
              return pl.projects.map((pr) => {
                const tests = pl.tests.filter((t) => t.projectId === pr.projectId);
                const rows = tests.slice(0, Math.max(0, MAX_TESTS_SHOWN - shown));
                shown += rows.length;
                return h(
                  "section",
                  { class: "ap-project" },
                  h("h3", { class: "ap-project-name" }, pr.name, h("span", { class: "ap-count" }, ` ${plural(pr.tests, "test")}${pr.all ? " (all)" : ""}`)),
                  h(
                    "ul",
                    null,
                    rows.map((t) => h("li", { class: "ap-test" }, h("span", { class: "ap-test-name" }, testName(t.projectId, t.id)), h("span", { class: "visually-hidden" }, ": "), h("span", { class: "ap-reason" }, t.reasons.join("; ")))),
                    tests.length > rows.length ? h("li", { class: "ap-more" }, `+${tests.length - rows.length} more`) : null,
                  ),
                );
              });
            }),
          ),
      ),
      h(
        "div",
        { class: "ap-foot" },
        h(
          "label",
          { class: "ap-check" },
          h("input", { type: "checkbox", checked: () => this.#coverage(), onchange: (e: Event) => this.#coverage.set((e.currentTarget as HTMLInputElement).checked) }),
          " Collect coverage",
        ),
        show(
          () => plan()?.needsImpactMap,
          () =>
            h(
              "label",
              { class: "ap-check" },
              h("input", { type: "checkbox", checked: () => this.#buildFirst(), onchange: (e: Event) => this.#buildFirst.set((e.currentTarget as HTMLInputElement).checked) }),
              " Build impact map first",
            ),
        ),
        h("span", { class: "ap-spacer" }),
        h(
          "button",
          { type: "button", class: "btn btn-small", title: "Fetch the changes again", onclick: () => this.#tick.update((n) => n + 1) },
          icon("arrowSync"),
          " Refresh",
        ),
        h("button", { type: "button", class: "btn btn-small", onclick: () => this.onDone() }, "Cancel"),
        h(
          "button",
          {
            type: "button",
            class: "btn btn-small btn-accent ap-run",
            disabled: () => !canRun(),
            onclick: run,
          },
          icon("playFilled"),
          () => {
            const p = plan();
            return p?.counts.tests ? ` Run ${plural(p.counts.tests, "test")}` : " Run";
          },
        ),
      ),
    ];
  }
}
define("lc-affected-preview", AffectedPreview);

declare global {
  interface HTMLElementTagNameMap {
    "lc-run-affected": RunAffected;
    "lc-affected-preview": AffectedPreview;
  }
}
