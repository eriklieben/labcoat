/**
 * <lc-project-diagnostics project-id="…">: why a project has no tests (P2-3). Replaces a disabled "Run 0 tests" with
 * the host's findings (TestProject.diagnostics: host exit + stderr tail, OutputType not Exe, no MTP runner, …), a
 * checklist of the usual causes and the tail of the project's discovery log, plus Rediscover / Open output actions.
 *
 * Placement (DETAIL / SHELL): the project node's detail view and the tree's empty state, whenever
 * `needsDiagnostics(project, testCount)` is true.
 */
import { button, codeBlock, computed, cx, define, h, icon, LabcoatElement, show, type Child, type IconName } from "@eriklieben/labcoat-design-system";
import type { ClientCommand, ProjectDiagnostic, TestProject } from "../contract";
import { getStore } from "../store";
import "./diagnostics.css";

/** Show the diagnostics view for this project: the host reported findings, or an idle project has no tests. */
export function needsDiagnostics(p: TestProject, testCount: number) {
  return !!p.diagnostics?.length || (testCount === 0 && p.status === "idle");
}

const LEVEL_ICON: Record<ProjectDiagnostic["level"], IconName> = { error: "errorCircle", warning: "warning", info: "info" };

interface CheckItem {
  id: string;
  title: string;
  how: Child;
  /** the host reported a matching finding */
  matches: (d: ProjectDiagnostic) => boolean;
}

const code = (s: string) => h("code", { class: "diag-code" }, s);

const CHECKS: CheckItem[] = [
  {
    id: "exe",
    title: "The test project is an executable",
    how: ["Microsoft.Testing.Platform test apps need ", code("<OutputType>Exe</OutputType>"), " (MSTest.Sdk and xunit.v3 set it for you)."],
    matches: (d) => /OutputType/i.test(d.message),
  },
  {
    id: "runner",
    title: "A Microsoft.Testing.Platform runner is enabled",
    how: ["Use MSTest.Sdk, or set ", code("<EnableMSTestRunner>true</EnableMSTestRunner>"), " (MSTest) / ", code("<EnableNUnitRunner>true</EnableNUnitRunner>"), " (NUnit), or reference xunit.v3 / TUnit."],
    matches: (d) => /runner/i.test(d.message),
  },
  {
    id: "host",
    title: "The test app starts with --server",
    how: ["Labcoat talks to the test app over the MTP server protocol. Try ", code("dotnet <test dll> --list-tests"), " to see whether it starts."],
    matches: (d) => /test host|did not connect|Discovery failed/i.test(d.message),
  },
  {
    id: "tests",
    title: "Test classes are public and marked as tests",
    how: ["MSTest: ", code("[TestClass]"), " + ", code("[TestMethod]"), "; and the build output is current."],
    matches: (d) => /No tests discovered/i.test(d.message),
  },
];

const LOG_LINES = 14;

/** A host finding shown under the checklist item it explains. */
function findingText(d: ProjectDiagnostic): Child {
  return h(
    "div",
    { class: "diag-finding" },
    h("div", { class: "diag-finding-msg" }, d.message),
    d.hint ? h("div", { class: "diag-hint" }, d.hint) : null,
    d.details ? h("details", { class: "diag-details" }, h("summary", null, "Test host output"), h("pre", { class: "diag-pre mono", tabIndex: 0 }, d.details)) : null,
    d.link ? docsLink(d.link) : null,
  );
}

/** "How to switch" for a finding with a docs page (TestProject.diagnostics[].link). */
const docsLink = (href: string) => h("a", { class: "diag-link", href, target: "_blank", rel: "noopener noreferrer" }, "How to switch");

/** The heading for a project Labcoat doesn't run (TestProject.testPlatform). */
const NOT_MTP_TITLE = { vstest: "uses VSTest", none: "is not a test app" } as const;

export interface ProjectDiagnosticsProps {
  projectId: string;
  /** "Open output" action (omitted = no button) */
  onOpenOutput: (() => void) | undefined;
}
export interface ProjectDiagnostics extends ProjectDiagnosticsProps {}
export class ProjectDiagnostics extends LabcoatElement<ProjectDiagnosticsProps> {
  static defaults: ProjectDiagnosticsProps = { projectId: "", onOpenOutput: undefined };
  static observedAttributes = ["project-id"];
  attributeChangedCallback(_a: string, _o: string | null, v: string | null) {
    this.projectId = v ?? "";
  }
  static hostClass = "project-diagnostics";

  protected render(): Child {
    const store = getStore();
    const $ = store.$;
    const project = computed(() => $.projects().find((p) => p.id === this.props.projectId()));
    const diags = computed(() => project()?.diagnostics ?? []);
    const busy = computed(() => {
      const s = project()?.status;
      return s === "building" || s === "discovering";
    });
    const logTail = computed(() => {
      const id = this.props.projectId();
      const lines = $.logs().filter((l) => l.projectId === id);
      return lines
        .slice(-LOG_LINES)
        .map((l) => (l.level === "info" ? l.message : `${l.level}: ${l.message}`))
        .join("\n");
    });
    const hostFailed = computed(() => diags().some((d) => d.level === "error" && /test host|did not connect/i.test(d.message)));
    // a project Labcoat doesn't run (VSTest): its own finding says how to switch; the generic checklist doesn't apply
    const notMtp = computed(() => project()?.testPlatform);

    const item = (d: () => ProjectDiagnostic) =>
      h(
        "li",
        { class: () => cx("diag-item", `level-${d().level}`) },
        () => icon(LEVEL_ICON[d().level] ?? "info", { size: 16, class: "diag-icon" }),
        h(
          "div",
          { class: "diag-text" },
          h("div", { class: "diag-message" }, () => d().message),
          show(
            () => d().hint,
            (hint) => h("div", { class: "diag-hint" }, hint),
          ),
          show(
            () => d().details,
            (details) =>
              h(
                "details",
                { class: "diag-details" },
                h("summary", null, "Test host output"),
                h("pre", { class: "diag-pre mono", tabIndex: 0 }, details),
              ),
          ),
          show(
            () => d().link,
            (link) => docsLink(link()),
          ),
        ),
      );

    return [
      h(
        "div",
        { class: "diag-head" },
        h("div", { class: "empty-illustration diag-art", "aria-hidden": "true" }, () => icon(hostFailed() ? "errorCircle" : "beaker", { size: 28 })),
        h(
          "div",
          { class: "diag-titles" },
          h("h3", { class: "diag-title" }, () => {
            const name = project()?.name ?? this.props.projectId();
            const tp = notMtp();
            if (tp) return `${name} ${NOT_MTP_TITLE[tp]}`;
            if (busy()) return `Discovering tests in ${name}…`;
            return hostFailed() ? `${name}: the test app did not start` : `No tests discovered in ${name}`;
          }),
          h("div", { class: "diag-sub" }, () =>
            notMtp()
              ? "Labcoat runs Microsoft.Testing.Platform test apps only, so it doesn't build or start this project. Switch it, then rediscover."
              : "Labcoat found the project but no tests. The checks below usually explain why; fix the project, then rediscover.",
          ),
        ),
      ),
      // one list (#review 2): the host's findings that match a known cause sit on that checklist item (flagged, first);
      // findings without one are listed as their own items above the remaining checks
      h(
        "section",
        { class: "diag-section" },
        h("h4", { class: "diag-section-title" }, () => (diags().length ? "What the host found" : "What to check")),
        h(
          "ul",
          { class: "diag-checklist" },
          () => {
            const ds = diags();
            const matched = new Set<ProjectDiagnostic>();
            const flagged = CHECKS.map((c) => {
              const hits = ds.filter((d) => c.matches(d));
              hits.forEach((d) => matched.add(d));
              return { c, hits };
            });
            const unmatched = ds.filter((d) => !matched.has(d));
            const check = ({ c, hits }: { c: CheckItem; hits: ProjectDiagnostic[] }) =>
              h(
                "li",
                { class: cx("diag-check", hits.length > 0 && "flagged") },
                hits.length
                  ? icon(LEVEL_ICON[hits[0].level] ?? "dismissCircle", { size: 16, class: cx("diag-check-icon", `level-${hits[0].level}`), title: "The host reported a problem here" })
                  : icon("circle", { size: 16, class: "diag-check-icon", title: "Check this" }),
                h(
                  "div",
                  { class: "diag-text" },
                  h("div", { class: "diag-message" }, c.title),
                  hits.map((d) => findingText(d)),
                  h("div", { class: "diag-hint" }, c.how),
                ),
              );
            return [
              ...unmatched.map((d) => item(() => d)),
              ...flagged.filter((x) => x.hits.length).map(check),
              ...(notMtp() ? [] : flagged.filter((x) => !x.hits.length).map(check)),
            ];
          },
        ),
      ),
      h(
        "section",
        { class: "diag-section" },
        h("h4", { class: "diag-section-title" }, "Discovery log"),
        show(
          () => !!logTail(),
          () => codeBlock({ code: logTail, label: "Discovery log (last lines)", class: "diag-log", wrap: true, maxHeight: 240 }),
          () => h("div", { class: "diag-hint" }, "No log lines for this project yet."),
        ),
      ),
      h(
        "div",
        { class: "diag-actions" },
        button(
          {
            variant: "accent",
            icon: "arrowSync",
            disabled: busy,
            onclick: () => store.send({ type: "discover", projectId: this.props.projectId.peek() } as ClientCommand),
          },
          " Rediscover",
        ),
        show(
          () => this.props.onOpenOutput(),
          (open) => button({ icon: "console", onclick: () => open()() }, " Open output"),
        ),
      ),
    ];
  }
}

define("lc-project-diagnostics", ProjectDiagnostics);

declare global {
  interface HTMLElementTagNameMap {
    "lc-project-diagnostics": ProjectDiagnostics;
  }
}
