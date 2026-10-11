/**
 * Build errors (P0-2): the parsed MSBuild diagnostics of a project whose last build failed (TestProject.buildErrors),
 * like Visual Studio's Error List. Opened from the "Build failed" message bar and from the project row's
 * "N build errors" pill (`openBuildErrors(projectId)`), shown in a modal dialog hosted by the shell.
 *
 * Clicking a file:line expands that location inline under the error (a code view with the error annotated, like VS's
 * Error List preview) and selects the project in the tree (`requestOpenSource()` → window event "labcoat:open-source",
 * handled by the shell in app.ts); the editor button opens it in the user's editor.
 */
import { button, computed, define, h, icon, LabcoatElement, list, show, signal, type Child } from "@eriklieben/labcoat-design-system";
import "./code-view";
import type { BuildDiagnostic, TestProject } from "../contract";
import { getStore, openInEditor } from "../store";
import { appDialog } from "./shortcuts";
import "./build-errors.css";

// ---- app-wide requests -------------------------------------------------------------------------------

const OPEN_SOURCE_EVENT = "labcoat:open-source";
export interface OpenSourceRequest {
  path: string;
  line?: number;
  column?: number;
  /** tree node to select (the project of a build error) */
  selectKey?: string;
}

/** Ask the shell to follow a source location (it selects `selectKey` in the tree; the dialog shows the source inline). */
export function requestOpenSource(req: OpenSourceRequest) {
  window.dispatchEvent(new CustomEvent<OpenSourceRequest>(OPEN_SOURCE_EVENT, { detail: req }));
}
export function onOpenSourceRequest(handler: (req: OpenSourceRequest) => void): () => void {
  const fn = (e: Event) => handler((e as CustomEvent<OpenSourceRequest>).detail);
  window.addEventListener(OPEN_SOURCE_EVENT, fn);
  return () => window.removeEventListener(OPEN_SOURCE_EVENT, fn);
}

/** Project whose build errors dialog is open (undefined = closed). */
export const buildErrorsFor = signal<string | undefined>(undefined);
export const openBuildErrors = (projectId: string) => buildErrorsFor.set(projectId);

export function errorCount(p: TestProject) {
  return p.buildErrorCount ?? p.buildErrors?.filter((d) => d.severity === "error").length ?? 0;
}

const fileName = (p: string) => p.split(/[\\/]/).pop() ?? p;
/** ":12" for a line or column number, "" without one. */
const lineSuffix = (n: number | undefined) => (n ? `:${n}` : "");
/** "CS0103: " before a message, "" without a code. */
const codePrefix = (code: string | undefined) => (code ? `${code}: ` : "");
/** "1 warning", "2 warnings". */
const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;

/**
 * The error count the Output rail badge and the output strip show: while builds are failing, the MSBuild errors the
 * build banner counts (one source, so "Output 1" never sits under "2 errors"); else the unseen error log lines.
 */
export const outputErrorCount = computed(() => {
  const $ = getStore().$;
  if ($.unseenErrors() === 0) return 0;
  const build = $.projects().reduce((a, p) => a + (p.status === "error" ? errorCount(p) : 0), 0);
  return build || $.unseenErrors();
});

// ---- list element ----------------------------------------------------------------------------------------

export interface BuildErrorsProps {
  project: TestProject | undefined;
  /** called after a location was opened (the dialog closes) */
  onOpened: () => void;
}
export interface BuildErrors extends BuildErrorsProps {}

/** `<lc-build-errors>`: the error list of one project (usable on its own, e.g. in a project detail view). */
export class BuildErrors extends LabcoatElement<BuildErrorsProps> {
  static defaults: BuildErrorsProps = { project: undefined, onOpened: () => {} };
  static hostClass = "build-errors";

  protected render(): Child {
    this.style.display = "block";
    const diags = computed(() => this.props.project()?.buildErrors ?? []);
    // file:line opens the source inline under the error (DETAIL's code view), like VS's Error List preview; the
    // window event lets the shell follow (selects the project in the tree).
    const shown = signal<string | undefined>(undefined);
    const keyOf = (d: BuildDiagnostic) => `${d.severity}|${d.file ?? ""}|${d.line ?? ""}|${d.column ?? ""}|${d.code ?? ""}|${d.message}`;
    const open = (d: BuildDiagnostic) => {
      if (!d.file) return;
      const k = keyOf(d);
      shown.set(shown.peek() === k ? undefined : k);
      const p = this.props.project.peek();
      requestOpenSource({ path: d.file, line: d.line, column: d.column, selectKey: p ? `p:${p.id}` : undefined });
    };
    // The file:line button (inline source) + editor button + project, or only the project when there's no file.
    const location = (x: BuildDiagnostic): Child => {
      const project = x.project ? h("span", { class: "be-project" }, fileName(x.project)) : null;
      if (!x.file) return project ? h("div", { class: "be-loc" }, project) : null;
      const file = x.file;
      return h(
        "div",
        { class: "be-loc" },
        h(
          "button",
          {
            class: "be-file mono",
            title: `Show ${file}${lineSuffix(x.line)}`,
            "aria-expanded": () => shown() === keyOf(x),
            onclick: () => open(x),
          },
          `${fileName(file)}${lineSuffix(x.line)}${lineSuffix(x.column)}`,
        ),
        h(
          "button",
          {
            class: "be-editor icon-btn small",
            title: "Open in editor",
            "aria-label": `Open ${fileName(file)} in editor`,
            onclick: () => void openInEditor(file, x.line, x.column),
          },
          icon("open", { size: 14 }),
        ),
        project,
      );
    };
    return show(
      () => diags().length > 0,
      () =>
        h(
          "ul",
          { class: "build-error-list", "aria-label": "Build errors" },
          list(
            diags,
            keyOf,
            (d) => {
              const x = d.peek();
              return h(
                "li",
                { class: `build-error sev-${x.severity}` },
                icon(x.severity === "error" ? "errorCircle" : "warning", { size: 16, class: "be-icon" }),
                h(
                  "div",
                  { class: "be-body" },
                  h("div", { class: "be-message" }, x.code ? h("span", { class: "be-code mono" }, x.code) : null, x.message),
                  location(x),
                  show(
                    () => shown() === keyOf(x) && !!x.file,
                    () =>
                      h(
                        "div",
                        { class: "be-source" },
                        h("lc-code-view", {
                          viewClass: "inline",
                          path: x.file,
                          focusLine: x.line,
                          markers: x.line ? [x.line] : [],
                          annotations: x.line ? [{ line: x.line, title: `${codePrefix(x.code)}${x.message}` }] : [],
                        }),
                      ),
                  ),
                ),
              );
            },
          ),
        ),
      () => h("pre", { class: "build-output mono" }, () => this.props.project()?.buildOutput ?? this.props.project()?.lastError ?? "No build output."),
    );
  }
}
define("lc-build-errors", BuildErrors);

declare global {
  interface HTMLElementTagNameMap {
    "lc-build-errors": BuildErrors;
  }
}

/** The modal dialog (rendered once by the shell): the requested project first, then other failed builds. */
export function buildErrorsDialog(): Child {
  const store = getStore();
  const failed = computed(() => {
    const id = buildErrorsFor();
    if (!id) return [];
    const ps = store.$.projects().filter((p) => p.status === "error" || p.id === id);
    return [...ps.filter((p) => p.id === id), ...ps.filter((p) => p.id !== id)];
  });
  const close = () => buildErrorsFor.set(undefined);
  return appDialog({
    open: () => !!buildErrorsFor(),
    onClose: close,
    class: "build-errors-dialog",
    size: "lg",
    title: () => {
      const ps = failed();
      const e = ps.reduce((a, p) => a + errorCount(p), 0);
      const w = ps.reduce((a, p) => a + (p.buildWarningCount ?? 0), 0);
      const who = ps.length === 1 ? `${ps[0].name}: ` : "";
      const warnings = w ? `, ${plural(w, "warning")}` : "";
      return `${who}${plural(e, "build error")}${warnings}`;
    },
    // Rebuild right here (#review 2): fix the code, then rebuild without hunting for the toolbar
    footer: () =>
      h(
        "div",
        { class: "build-errors-footer" },
        h("span", { class: "dim" }, "Fix the errors in your editor, then rebuild."),
        button(
          {
            variant: "accent",
            icon: "arrowSync",
            title: "Rebuild and rediscover tests",
            disabled: () => !!store.$.currentRun() || store.$.conn() !== "open",
            onclick: () => {
              const ids = failed.peek().filter((p) => p.status === "error").map((p) => p.id);
              for (const id of ids.length ? ids : [buildErrorsFor.peek()!]) store.send({ type: "discover", projectId: id });
              close();
            },
          },
          " Rebuild",
        ),
      ),
    content: () =>
      list(
        failed,
        (p) => p.id,
        (p) =>
          h(
            "section",
            { class: "build-errors-project" },
            show(
              () => failed().length > 1,
              () => h("h3", { class: "build-errors-heading" }, () => `${p().name} · ${errorCount(p())} error${errorCount(p()) === 1 ? "" : "s"}`),
            ),
            h("lc-build-errors", { project: p, onOpened: close }),
          ),
      ),
  });
}
