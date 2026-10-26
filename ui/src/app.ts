/**
 * <lc-app>: the app shell (port of App.tsx): header, nav rail, message bars, toolbar, run summary, progress bar and
 * the workspace (Tests page: tree | details/coverage, Coverage page, Settings page, output panel).
 *
 * Each pane lives in a small slot function below. Shell features: suite status headline + composition bar
 * (components/suite-status.ts), watch strip, build errors dialog, command palette (Ctrl/Cmd+K), keyboard shortcuts (`?`),
 * deep links in the hash (#/tests?t=<projectId>|<id>&f=failed&sort=slowest&run=<runId>), never-connected page.
 */
import {
  animateEnter,
  announce,
  batch,
  button,
  copyButton,
  computed,
  cx,
  define,
  effect,
  h,
  icon,
  keyed,
  LabcoatElement,
  onCleanup,
  reveal,
  show,
  signal,
  slideIndicator,
  tabIds,
  tabs,
  toast,
  untracked,
  type Child,
} from "@eriklieben/labcoat-design-system";
import type { BuildDiagnostic, ClientCommand, CommandRejectedEvent, CoverageReport, ImpactMapInfo, RunDetails, RunSummary, TestNode } from "./contract";
import { editorNotice, getStore, serverEventListeners, testKey, type CoverageLabel, type CoverageTarget } from "./store";
import { followTheme } from "./theme";
import { bucket, createTreeBuilder, indexNodes, leaves, regroup, type StatusFilter, type TreeNode, type TreeSort } from "./tree";
import { formatRunTime, keepFocus, loadPref, pct, rateTone, runCounts, savePref } from "./util";
import { attachSteps, debugAvailable, debugSession, debugTests, endDebugSession } from "./debug";
import { celebrate } from "./confetti";
import { getSettings, settings } from "./settings";
// SHELL
import { onSelectTestRequest } from "./components/coverage-line-tests";
import { requestCompare } from "./components/run-switcher"; // RUNS
import { nextFailureKey } from "./components/run-overview";
import { brandLogo, isMac, menuButton } from "@eriklieben/labcoat-design-system";
import { buildErrorsDialog, errorCount, onOpenSourceRequest, openBuildErrors, outputErrorCount } from "./components/build-errors";
import { commandPalette, paletteOpen } from "./components/command-palette";
import { neverConnectedPage } from "./components/never-connected";
import { installShortcuts, shortcutsDialog, shortcutsOpen } from "./components/shortcuts";
import { followDocumentStatus, runAnnouncer, runSummaryBar, suiteBar, suiteModel } from "./components/suite-status";
import { focusTree, multiSelection, nodeLink, selectionTests, SORT_LABELS, statusFilter, treeFilterPass, treeFiltered, treeGroup, treeSort } from "./components/tree-pane";
import "./components/watch-strip";
import "./components/settings-page";
import "./components/coverage-page"; // COVERAGE (also defines lc-coverage-pane)
import "./components/log-panel"; // COVERAGE
import "./components/detail-pane"; // DETAIL
import { getLiveOutput } from "./live-output"; // LIVE OUTPUT
import "./components/run-affected"; // AFFECTED (git)
import { snapshotToolbarButton } from "./components/snapshot-view"; // SNAPSHOTS (Verify)

type Page = "tests" | "coverage" | "settings";

export class LabcoatApp extends LabcoatElement {
  static readonly hostClass = "layout";

  protected render(): Child {
    const $ = getStore().$;
    followTheme();
    // Never connected (the first attempt failed): one full-page state instead of an empty shell.
    const neverConnected = computed(() => $.transport() === "ws" && !$.everConnected() && $.conn() === "reconnecting");
    return keyed(neverConnected, (nc) => (nc ? neverConnectedPage() : this.renderApp()));
  }

  private renderApp(): Child {
    const store = getStore();
    const $ = store.$;

    // ---- view state ------------------------------------------------------------------------------
    // Right-pane tab of the Tests page.
    const view = signal<"details" | "coverage">(loadPref("view", "details"));
    effect(() => savePref("view", view()));
    // Page (nav rail): Tests workspace, overall Coverage report, Settings. Mirrored in the URL hash (#/coverage).
    // Deep links (F4): page, selected node, status filter and viewed run live in the hash:
    //   #/tests?t=<projectId>|<testId>&f=failed&run=<runId>   (n=<node key> for groups)
    const link0 = parseHash();
    const page = signal<Page>(link0.page ?? loadPref<Page>("page", "tests"));
    if (link0.filter) statusFilter.set(link0.filter);
    if (link0.sort) treeSort.set(link0.sort);
    const pageCovPath = signal<string | undefined>(undefined);
    const covPath = signal<string | undefined>(undefined);
    const covFocus = signal<number | undefined>(undefined);
    const openCovPath = (p: string | undefined) =>
      batch(() => {
        covPath.set(p);
        covFocus.set(undefined);
      });

    // Viewing a past run: the Tests page shows that run's outcomes/durations/errors (read-only snapshot); tests that
    // weren't part of it are shown muted as "not in this run".
    const viewTests = computed(() => {
      const viewing = $.viewing();
      return viewing?.details ? snapshotTests($.tests(), viewing.details) : $.tests();
    });
    // Incremental: a result update patches the changed leaves + their ancestors instead of rebuilding (P2-13).
    const treeBuilder = createTreeBuilder();
    // quarantined failing tests count apart from "failed" (tree counts, suite status, Run failed)
    const quarantine = computed(() => {
      const q = $.quarantined();
      return q?.length ? new Set(q) : undefined;
    });
    const roots = computed(() =>
      $.viewing()
        ? treeBuilder.build($.projects(), viewTests(), undefined, undefined, quarantine())
        : treeBuilder.build($.projects(), $.tests(), $.pending(), $.testsDelta(), quarantine()),
    );
    // What the tree shows: the roots regrouped (Group by: Project / Trait / Outcome; Namespace = the roots as built).
    const shownRoots = computed(() => regroup(roots(), treeGroup()));
    // synthetic group nodes of a non-default grouping (selectable: the detail pane shows their summary)
    const groupIndex = computed(() => (treeGroup() === "namespace" ? undefined : indexNodes(shownRoots())));
    // patched in place: always read it through roots() (see createTreeBuilder)
    const index = {
      get: (k: string) => {
        roots();
        return treeBuilder.index().get(k) ?? (k.startsWith("g:") ? groupIndex()?.get(k) : undefined);
      },
    };

    const selectedKey = signal<string | undefined>(link0.key ?? loadPref<string | undefined>("selected", undefined));
    effect(() => savePref("selected", selectedKey()));
    // LIVE OUTPUT: "Follow running test" selects the test that started running (live-output.ts)
    effect(() => {
      const k = getLiveOutput().followTarget();
      if (k) untracked(() => selectedKey.set(k));
    });
    effect(() => savePref("page", page()));
    // A run in the link is opened once the state is there.
    let pendingRun = link0.run;
    effect(() => {
      if (!$.loaded() || !pendingRun) return;
      const id = pendingRun;
      pendingRun = undefined;
      untracked(() => store.viewRun(id));
    });
    // Write the hash on change (replaceState: no history entries per click).
    effect(() => {
      const hash = buildHash(page(), selectedKey(), statusFilter(), $.viewing()?.run.runId, treeSort());
      if (location.hash !== hash) history.replaceState(null, "", `${location.pathname}${location.search}${hash}`);
    });
    listen(window, "hashchange", () => {
      const l = parseHash();
      batch(() => {
        if (l.page) page.set(l.page);
        if (l.key) selectedKey.set(l.key);
        statusFilter.set(l.filter ?? "all");
        if (l.sort) treeSort.set(l.sort);
      });
      if (l.run !== store.get().viewing?.run.runId) store.viewRun(l.run);
    });
    const selected = computed(() => {
      const k = selectedKey();
      return k ? index.get(k) : undefined;
    });

    // ---- output panel ----------------------------------------------------------------------------
    // P1-5: closed by default (the rail badge / status tell when something needs attention).
    const logOpen = signal(loadPref("logOpen", false));
    // Settings → "Open the output panel on errors".
    let prevErrors = $.unseenErrors.peek();
    effect(() => {
      const unseen = $.unseenErrors();
      if (unseen > prevErrors && settings().openOutputOnError) logOpen.set(true);
      prevErrors = unseen;
    });
    effect(() => {
      const open = logOpen();
      $.unseenErrors();
      savePref("logOpen", open);
      store.logPanelOpen = open;
      if (open) store.markLogsSeen();
    });

    const send = (c: ClientCommand) => store.send(c);
    const running = computed(() => !!$.currentRun());
    const connected = computed(() => $.conn() === "open");

    // ---- motion: heavy-run gate (MOTION.md) ------------------------------------------------------
    // html.lc-busy while a run of more than 200 tests is in flight, and while the tab is hidden: decorative motion
    // (list enter, sheen, count-up, shimmer) is off; state motion (progress, spinners, status pop, P6 flash) stays.
    const hidden = signal(document.visibilityState === "hidden");
    const onVis = () => hidden.set(document.visibilityState === "hidden");
    document.addEventListener("visibilitychange", onVis);
    onCleanup(() => document.removeEventListener("visibilitychange", onVis));
    const busy = computed(() => {
      const r = $.currentRun();
      return hidden() || (!!r && Math.max(r.total, $.runKeys()?.size ?? 0) > 200);
    });
    effect(() => document.documentElement.classList.toggle("lc-busy", busy()));
    onCleanup(() => document.documentElement.classList.remove("lc-busy"));
    const busyProjects = computed(() => $.projects().filter((p) => p.status === "building" || p.status === "discovering"));
    const runDisabled = computed(() => running() || !connected());

    const runTests = (list: TestNode[]) => {
      if (!list.length) return;
      const pids = new Set(list.map((t) => t.projectId));
      send({ type: "run", projectId: pids.size === 1 ? list[0].projectId : undefined, testIds: list.map((t) => t.id) });
    };
    const coverageTests = (tests: TestNode[], title: string) => {
      if (!tests.length) return;
      batch(() => {
        page.set("tests");
        view.set("coverage");
      });
      const pids = new Set(tests.map((t) => t.projectId));
      store.requestCoverage({ title, context: `${tests.length} test${tests.length === 1 ? "" : "s"}` }, tests.map((t) => t.id), pids.size === 1 ? tests[0].projectId : undefined);
    };
    // "Run N matching": the toolbar runs what the filtered tree shows (the tree pane's filter pass: one walk per keystroke)
    const matching = computed(() => (treeFiltered() ? treeFilterPass(shownRoots()).tests : undefined));
    const debugNode = (n: TreeNode) => {
      const list = leaves(n);
      debugTests(list, n.test ? n.test.displayName : `${n.label} (${list.length} tests)`);
    };
    const runNode = (n: TreeNode, only?: TestNode[]) => {
      if (only) return runTests(only);
      if (n.kind === "project") return send({ type: "run", projectId: n.projectId });
      runTests(leaves(n));
    };

    // ---- per-test coverage -----------------------------------------------------------------------
    const coverageFor = (n: TreeNode) => {
      batch(() => {
        page.set("tests");
        view.set("coverage");
      });
      if (n.kind === "project") {
        store.requestCoverage({ title: n.label, context: "All tests in project" }, [], n.projectId);
        return;
      }
      const target = coverageTargetFor(n);
      if (target) store.requestCoverage(target.label, target.testIds, target.projectId);
    };

    // Coverage tab: selecting a node shows its cached coverage (GET /api/coverage/test) — a single click never runs.
    // Keyboard navigation is debounced so arrowing through the tree doesn't fire a lookup per row.
    let lastNavKey = 0;
    listen(window, "keydown", (e) => {
      if (/^(Arrow|Home|End|Page)/.test(e.key)) lastNavKey = performance.now();
    }, true);
    const selectionSig = computed(() => {
      const s = selected();
      if (!s) return "";
      return `${s.key}#${s.kind === "project" ? "" : leaves(s).length}`;
    });
    effect(() => {
      const v = view();
      selectionSig();
      if (v !== "coverage") return;
      untracked(() => {
        const n = selected();
        const viaKeyboard = performance.now() - lastNavKey < 300;
        store.selectCoverage(n && n.kind !== "project" ? coverageTargetFor(n) : undefined, viaKeyboard ? 150 : 0);
      });
    });

    // When a subset report is shown (a per-test run finished or a cached one was selected), open the product file it
    // covered most, at its first covered line; leaving a subset report returns to the file list.
    let autoOpened: CoverageReport | undefined;
    effect(() => {
      const coverage = $.coverage();
      untracked(() => {
        const prev = autoOpened;
        autoOpened = coverage;
        if (prev === coverage) return;
        if (!coverage || (coverage.scope ?? "all") !== "subset") {
          if (prev && (prev.scope ?? "all") === "subset") openCovPath(undefined);
          return;
        }
        let best: (typeof coverage.files)[number] | undefined;
        for (const f of coverage.files) if (f.linesCovered > 0 && (!best || f.linesCovered > best.linesCovered)) best = f;
        batch(() => {
          covPath.set(best?.path);
          covFocus.set(best?.lines.find((l) => l.hits > 0)?.line);
        });
      });
    });

    // ---- celebration -----------------------------------------------------------------------------
    // Celebrate a finished run where everything passed (≥ 3 tests, nothing failed or cancelled; skipped is fine), but
    // only when it's news: the first green run of these tests, or the run that turns them from failing to all green.
    // Whether they were green before is read from each test's persisted history, so a reload or a Labcoat restart
    // doesn't bring the confetti back. Opt-in (Settings → Celebrations). The decision is made once per run, when that
    // run's history entries are in the store; if they're not there yet, a timer re-evaluates against the latest state.
    let celebrated: string | undefined;
    let celebrateTimer: { runId: string; id: number } | undefined;
    const celebrating = signal(false);
    effect(() => {
      const lastRun = $.lastRun();
      const currentRun = $.currentRun();
      $.tests();
      untracked(() => {
        if (!lastRun || currentRun || celebrated === lastRun.runId || !store.sawRunStart(lastRun.runId)) return;
        const runId = lastRun.runId;
        const decide = (final: boolean) => {
          const s = store.get();
          if (celebrated === runId || s.currentRun || s.lastRun?.runId !== runId) return;
          const verdict = celebrationVerdict(s.lastRun, s.tests);
          if (verdict === "wait" && !final) {
            if (celebrateTimer?.runId !== runId) celebrateTimer = { runId, id: window.setTimeout(() => decide(true), 1500) };
            return;
          }
          celebrated = runId;
          if (celebrateTimer) window.clearTimeout(celebrateTimer.id);
          celebrateTimer = undefined;
          // "wait" after the grace period: no history arrived (e.g. a host without history) -> treat as news.
          if (verdict === "no" || !getSettings().confetti) return;
          // Burst from the middle of the main panel (Test details / Coverage, or the Coverage / Settings page).
          const origin = document.querySelector(".page-view") ?? document.querySelector(".detail-pane") ?? document.querySelector(".workspace");
          celebrate(origin?.getBoundingClientRect());
          celebrating.set(true);
          window.setTimeout(() => celebrating.set(false), 2400);
        };
        decide(false);
      });
    });
    onCleanup(() => window.clearTimeout(celebrateTimer?.id));

    // Run failed: the list is only built when clicked; the count comes from the tree's aggregates (no per-frame scan).
    const failedNow = () => {
      const q = new Set(store.get().quarantined ?? []);
      return [...store.get().tests.values()].filter((t) => bucket(t.outcome) === "failed" && !q.has(testKey(t)));
    };
    const failedCount = computed(() => roots().reduce((a, r) => a + r.counts.failed, 0));

    // ---- suite status (headline, composition bar, title/favicon, announcer) ------------------------
    const suite = suiteModel(roots);
    followDocumentStatus(suite);

    // ---- splitters -------------------------------------------------------------------------------
    // Tree width as a percentage of the workspace (P2-7): 36% fits 1000 px and 1440 px windows alike.
    const treeW = signal(loadPref("treeWidthPct", 36));
    const logH = signal(loadPref("logHeight", 220));
    effect(() => savePref("treeWidthPct", treeW()));
    effect(() => savePref("logHeight", logH()));
    let main!: HTMLElement;
    const dragTree = (e: PointerEvent) => {
      const startX = e.clientX;
      const width = main.clientWidth - 24 || 1200;
      const start = (treeW.peek() / 100) * width;
      const max = width - 320;
      drag(e, (ev) => treeW.set(Math.round((Math.max(280, Math.min(max, start + ev.clientX - startX)) / width) * 1000) / 10));
    };
    const dragLog = (e: PointerEvent) => {
      const startY = e.clientY;
      const start = logH.peek();
      const max = (main.clientHeight || 800) - 200;
      drag(e, (ev) => logH.set(Math.max(100, Math.min(max, start - (ev.clientY - startY)))));
    };

    const errorProjects = computed(() => $.projects().filter((p) => p.status === "error"));
    // the Output badge counts what the build banner counts (MSBuild errors) while builds are failing, else unseen error lines
    const buildErrorTotal = computed(() => errorProjects().reduce((a, p) => a + errorCount(p), 0));
    const badgeErrors = outputErrorCount;
    const rebuild = (ids: string[]) => {
      if (!ids.length) return send({ type: "discover" });
      for (const id of ids) send({ type: "discover", projectId: id });
    };
    const disconnected = computed(() => $.everConnected() && $.conn() !== "open");

    // an "open in editor" failure notice disappears by itself after a while
    effect(() => {
      if (!editorNotice()) return;
      const id = window.setTimeout(() => editorNotice.set(undefined), 10000);
      onCleanup(() => window.clearTimeout(id));
    });

    // ---- keyboard: global shortcuts (P1-7), F8 failure navigation, command palette (F7) ---------------
    /** After a navigation from the palette: focus the page (tree on Tests, the page heading elsewhere). */
    const focusPage = (p: Page) =>
      requestAnimationFrame(() => {
        if (p === "tests") return focusTree();
        const hd = this.querySelector<HTMLElement>(".workspace h1, .workspace h2");
        if (!hd) return;
        if (!hd.hasAttribute("tabindex")) hd.tabIndex = -1;
        hd.focus({ preventScroll: true });
      });
    const selectAndShow = (key: string) =>
      batch(() => {
        page.set("tests");
        view.set("details");
        selectedKey.set(key);
      });
    /** F8 / Shift+F8: next / previous failure in tree order (wraps); from nothing, the first NEW failure (DETAIL's helper). */
    const nextFailure = (dir: 1 | -1) => {
      const cur = selectedKey.peek();
      const key = nextFailureKey(roots.peek(), cur?.startsWith("t:") ? cur : undefined, dir);
      if (!key) return announce("No failures");
      selectAndShow(key);
      // focus follows the failure (the tree's active row), and a screen reader hears where it landed
      queueMicrotask(() => focusTree());
      const fails: string[] = [];
      const walk = (x: TreeNode): void => {
        if (x.test) {
          if (bucket(x.test.outcome) === "failed") fails.push(x.key);
        } else if (x.counts.failed) x.children.forEach(walk);
      };
      roots.peek().forEach(walk);
      const t = index.get(key)?.test;
      if (!t) return;
      const firstLine = t.errorMessage ? ` — ${t.errorMessage.split("\n")[0].slice(0, 160)}` : "";
      announce(`Failure ${fails.indexOf(key) + 1} of ${fails.length}: ${t.displayName}${firstLine}`);
    };
    const toggleWatch = () => {
      if (connected.peek()) send({ type: "setWatch", enabled: !$.watch.peek() });
    };
    const runAll = () => {
      if (!runDisabled.peek() && $.tests.peek().size) send({ type: "run" });
    };
    /** the toolbar's primary action: everything, or the N tests the filtered tree shows */
    const runPrimary = () => {
      const m = matching.peek();
      if (m) runTests(m);
      else runAll();
    };
    installShortcuts({
      runAll,
      toggleWatch,
      nextFailure,
      openPalette: () => paletteOpen.set(true),
      toggleOutput: () => logOpen.update((v) => !v),
      // Esc never cancels a run (it would also stop the warm host): only Stop, Ctrl/Cmd+. and the palette do
      escape: () => {
        if (store.get().viewing) store.viewRun(undefined);
      },
      stop: () => {
        if (running.peek()) send({ type: "cancel" });
      },
    });
    // Build error file:line → select the project in the tree (the source itself shows inline in the dialog, see build-errors.ts).
    onCleanup(
      onOpenSourceRequest((r) => {
        if (r.selectKey) selectAndShow(r.selectKey);
      }),
    );
    // COVERAGE's line-tests popover: "Select" = select the test and show its details
    onCleanup(onSelectTestRequest((r) => selectAndShow(r.key)));

    // Round 4: the host refused a command (unknown tests, malformed, conflict): say why instead of nothing happening
    const onRejected = (e: { type: string } & Record<string, unknown>) => {
      if (e.type !== "commandRejected") return;
      const r = e as unknown as CommandRejectedEvent;
      const ids = r.unknownTestIds?.length;
      const what = r.command ? `“${r.command}” refused: ` : "";
      const unknown = ids ? ` (${plural(ids, "unknown test")})` : "";
      toast(`${what}${r.error ?? "the host rejected the command"}${unknown}`, { intent: "error" });
    };
    serverEventListeners.add(onRejected);
    onCleanup(() => serverEventListeners.delete(onRejected));
    effect(() => this.classList.toggle("is-running", running()));
    // Run all ⇄ Stop share a slot: the keyboard focus moves with the visible one (it never stays on a hidden button)
    effect(() => {
      const r = running();
      untracked(() =>
        queueMicrotask(() => {
          const a = document.activeElement as HTMLElement | null;
          if (!a?.closest?.(".run-slot") || a.getAttribute("aria-hidden") !== "true") return;
          this.querySelector<HTMLElement>(r ? ".run-slot .btn-danger" : ".run-slot .run-primary")?.focus({ preventScroll: true });
        }),
      );
    });
    // a control re-rendered or removed under the keyboard focus: the same control again, else the tree
    keepFocus(this, () => (page.peek() === "tests" ? this.querySelector<HTMLElement>(".tree-scroller") : this.querySelector<HTMLElement>(".page-view h2, .page-view [tabindex]")));

    // ---- slots for components not ported yet (phase 2) ---------------------------------------------
    // RUNS: <lc-run-switcher> reads runs/viewing/connection from the store; onJump = run diff rows → select the test.
    const runSwitcherSlot = () =>
      h("lc-run-switcher", { onJump: (key: string) => batch(() => {
          page.set("tests");
          view.set("details");
          selectedKey.set(key);
        }) });
    // CoveragePage({ report: fullCoverage, enabled: coverageEnabled, running, lastRun, openPath: pageCovPath,
    //   onOpen: pageCovPath.set, onRunAllWithCoverage: () => send({ type: "run", coverage: true }), fetchSource })
    const coveragePageSlot = () => h("lc-coverage-page", { openPath: pageCovPath, onOpen: (p: string | undefined) => pageCovPath.set(p) });
    const settingsPageSlot = () => h("lc-settings-page");
    // CoveragePane({ report: coverage, enabled: coverageEnabled, running, onEnable, onRunAll, onRunAllWithCoverage,
    //   openPath: covPath, focusLine: covFocus, onOpen: openCovPath, fetchSource, label: coverageLabel,
    //   request: coverageRequest, selection: coverageSelection, onCollect: (t) => store.requestCoverage(t.label, t.testIds, t.projectId),
    //   hasFull: !!fullCoverage && fullCoverage !== coverage, onShowFull: () => { store.showFullCoverage(); openCovPath(undefined) },
    //   onDismissRequest: () => store.dismissCoverageRequest() })
    const coveragePaneSlot = () => h("lc-coverage-pane", { openPath: covPath, focusLine: covFocus, onOpen: openCovPath });
    // DetailPane({ node: selected, onRun: runNode, onSelect: selectedKey.set, onRediscover: (projectId) => send({ type: "discover", projectId }),
    //   runDisabled, aspireDashboardUrl, viewRunId: viewing?.run.runId, snapshot: viewing?.status === "ready" ? viewing.run : undefined,
    //   onViewRun: (id, fallback) => store.viewRun(id, fallback), coverage, fetchSource })
    // DETAIL: <lc-detail-pane> reads coverage/viewing/runs/aspire/tracing from the store; empty selection = run overview.
    const detailPaneSlot = () => h("lc-detail-pane", { node: selected, onRun: (n: TreeNode, only?: TestNode[]) => runNode(n, only), onSelect: (k: string) => selectedKey.set(k) });
    // LogPanel({ logs, projects, onClose: () => logOpen.set(false), onClear: () => store.clearLogs() })
    const logPanelSlot = () => h("lc-log-panel", { onClose: () => logOpen.set(false) });

    // ---- DOM -------------------------------------------------------------------------------------
    // P12: the rail's active indicator slides between items (200 ms emphasized); the page content fades in with a
    // 4 px rise (P1's .lc-enter) — no exit, no view transition.
    const railItems = new Map<Page, HTMLElement>();
    let prevPage = untracked(page);
    effect(() => {
      const p = page();
      const from = railItems.get(prevPage);
      const to = railItems.get(p);
      prevPage = p;
      if (from && to && from !== to) queueMicrotask(() => slideIndicator(from, to, "vertical", "::before"));
    });
    let firstPage = true;
    const pageEnter = (el: Child): Child => {
      if (firstPage) {
        firstPage = false;
        return el;
      }
      return el instanceof HTMLElement ? animateEnter(el) : el;
    };
    // P5: the Test details / Coverage underline slides between the two tabs (built into the DS tabs())
    const paneIds = tabIds("pane");
    const tabPanelOf = (id: "details" | "coverage", el: Child): Child => {
      if (el instanceof HTMLElement) {
        el.setAttribute("role", "tabpanel");
        el.id = paneIds.panel(id);
        el.setAttribute("aria-labelledby", paneIds.tab(id));
      }
      return el;
    };
    const railLink = (p: Page, label: string, title: string, iconName: "taskList" | "dataPie" | "settings") =>
      h(
        "a",
        {
          ref: (el: HTMLAnchorElement) => railItems.set(p, el),
          class: () => cx("rail-item", page() === p && "active"),
          href: `#/${p}`,
          title,
          "aria-current": () => (page() === p ? "page" : undefined),
          onclick: (e) => {
            e.preventDefault();
            page.set(p);
          },
        },
        icon(iconName, { size: 22 }),
        h("span", { class: "rail-label" }, label),
      );

    const testsPageSlot = (): Child =>
      h(
        "div",
        { class: "split" },
        h("lc-tree-pane", {
          roots: shownRoots,
          selectedKey,
          onSelect: (k: string) => selectedKey.set(k),
          onRun: (n: TreeNode) => runNode(n),
          onCoverage: coverageFor,
          coverageMode: () => view() === "coverage",
          runDisabled,
          loaded: $.loaded,
          onRediscover: () => send({ type: "discover" }),
          onRunTests: (tests: TestNode[]) => runTests(tests),
          onCoverageTests: (tests: TestNode[], label: string) => coverageTests(tests, label),
          onDebug: debugNode, // a callback prop is passed as-is (not a reactive binding); the tree checks debugAvailable()
        }),
        h("div", {
          class: "splitter-v",
          onpointerdown: dragTree,
          role: "separator",
          "aria-orientation": "vertical",
          "aria-label": "Resize test tree",
        }),
        h(
          "div",
          { class: "right-col" },
          // DS tabs: one tab stop, ←/→ move and select, aria-controls → the panel (#6)
          tabs<"details" | "coverage">({
            ids: paneIds,
            controls: true,
            label: "Detail views",
            selected: view,
            onSelect: (v) => view.set(v),
            tabs: [
              { id: "details", label: "Test details", icon: "taskList" },
              {
                id: "coverage",
                // the Coverage view (shieldCheck stays for "collect coverage" actions, dataPie for the report page)
                icon: "coverageView",
                label: "Coverage",
                // P1-4: name what the percentage is about ("Coverage · Add_ReturnsSum 27%"), and only for the
                // selection's own report: the overall % next to a selected test would read as that test's coverage.
                pill: [
                  show(
                    () => ($.coverageSelection()?.status === "found" && $.coverage() ? $.coverageSelection()!.label.title : undefined),
                    (t) => h("span", { class: "tab-context", title: t }, () => ` · ${t()}`),
                  ),
                  show(
                    () => ($.coverageSelection()?.status === "found" ? $.coverage() : undefined),
                    (c) =>
                      h(
                        "span",
                        { class: () => cx("tab-pill", !c().noCoverableLines && c().linesValid > 0 && `tone-${rateTone(c().lineRate)}`) },
                        () => (c().noCoverableLines || c().linesValid === 0 ? "n/a" : pct(c().lineRate)),
                      ),
                  ),
                ],
              },
            ],
          }),
          keyed(paneShown, paneSlot),
        ),
      );
    // ---- command palette ------------------------------------------------------------------------------
    /** The palette's actions on the selected node: none while its project failed to build. */
    const selectionActions = (sel: TreeNode | undefined): { runSelected?: () => void; coverageSelected?: () => void; debugSelected?: () => void } => {
      if (!sel || isBuildBlocked(sel)) return {};
      return {
        runSelected: () => runNode(sel),
        coverageSelected: () => coverageFor(sel),
        debugSelected: (sel.test || sel.kind === "method") && debugAvailable() ? () => debugNode(sel) : undefined,
      };
    };
    const openFirstBuildErrors = () => openBuildErrors((errorProjects().find((p) => p.buildErrors?.length) ?? errorProjects()[0]).id);
    const copyLink = () => {
      const k = selectedKey.peek();
      const url = k && page.peek() === "tests" ? nodeLink(k) : location.href;
      void navigator.clipboard?.writeText(url).then(() => toast("Copied link", { intent: "success", icon: "link" }));
    };

    /** What the right column shows: the Coverage view, the test details, or "connecting" before the first snapshot. */
    const paneShown = (): "coverage" | "details" | "connecting" => {
      if (view() === "coverage") return "coverage";
      return $.loaded() || $.everConnected() ? "details" : "connecting";
    };
    const paneSlot = (v: "coverage" | "details" | "connecting"): Child => {
      if (v === "coverage") return tabPanelOf("coverage", coveragePaneSlot());
      return tabPanelOf("details", v === "details" ? detailPaneSlot() : connectingPane());
    };
    const pageSlot = (p: Page): Child => {
      if (p === "coverage") return coveragePageSlot();
      if (p === "settings") return settingsPageSlot();
      return testsPageSlot();
    };

    return [
      h(
        "header",
        { class: "app-header" },
        h("div", { class: "brand-logo" }, brandLogo({ size: 24, class: "brand-icon" })),
        h("a", { class: "application-name", href: "./", onclick: (e) => e.preventDefault() }, "Labcoat"),
        // P2-1: the workspace name tells two Labcoat tabs apart (also the page's h1).
        h(
          "div",
          { class: "page-title-slot" },
          h(
            "h1",
            { class: () => cx("page-header", !$.loaded() && "is-placeholder"), title: () => $.workspaceName() ?? "" },
            () => $.workspaceName() ?? ($.loaded() ? "Tests" : "Connecting…"),
          ),
        ),
        h("div", { class: "flex-filler" }),
        runSwitcherSlot(),
      ),

      h(
        "nav",
        { class: "nav-rail", "aria-label": "Main" },
        railLink("tests", "Tests", "Tests", "taskList"),
        railLink("coverage", "Coverage", "Coverage report (last full run)", "dataPie"),
        // pages at the top; panel toggles and settings at the bottom (Output sits just above Settings)
        h("span", { class: "rail-filler" }),
        h(
          "button",
          {
            class: () => cx("rail-item", logOpen() && "pressed"),
            title: "Output (Ctrl+`)",
            onclick: () => logOpen.update((v) => !v),
            "aria-pressed": () => logOpen(),
          },
          icon("console", { size: 22 }),
          h("span", { class: "rail-label" }, "Output"),
          // the badge's number is part of the name ("Output, 2 errors"), not an unlabeled "Output 2" (name from content:
          // the visible text comes first, WCAG 2.5.3)
          show(
            () => badgeErrors() > 0 && !logOpen(),
            () => [
              h("span", { class: "rail-badge", "aria-hidden": "true" }, () => (badgeErrors() > 99 ? "99+" : badgeErrors())),
              h("span", { class: "visually-hidden" }, () => `, ${badgeErrors()} error${badgeErrors() === 1 ? "" : "s"}`),
            ],
          ),
        ),
        railLink("settings", "Settings", "Settings", "settings"),
      ),

      h(
        "div",
        { class: "messagebar-container" },
        reveal(disconnected, () =>
          h(
            "div",
            { class: "messagebar mb-warning top", role: "status" },
            icon("plugDisconnected", { size: 18, class: "mb-icon" }),
            h(
              "div",
              { class: "mb-body inline" },
              h("span", { class: "mb-title" }, "Connection to the explorer host lost."),
              h("span", () => {
                const r = $.retryInMs();
                const reconnecting = r ? `Reconnecting in ${Math.ceil(r / 1000)}s…` : "Reconnecting…";
                return `${reconnecting} Results shown may be stale.`;
              }),
            ),
            h("button", { class: "btn btn-small mb-action", onclick: () => store.transport.retryNow?.() }, "Retry now"),
          ),
        ),
        reveal(editorNotice, (msg) =>
          h(
            "div",
            { class: "messagebar mb-warning top", role: "alert" },
            icon("warning", { size: 18, class: "mb-icon" }),
            h("div", { class: "mb-body inline" }, h("span", { class: "mb-title" }, msg)),
            h("button", { class: "btn btn-small mb-action", onclick: () => editorNotice.set(undefined) }, "Dismiss"),
          ),
        ),
        reveal(debugSession, (ds) =>
          h(
            "div",
            { class: () => cx("messagebar top debug-bar", ds().status === "error" ? "mb-error" : "mb-info"), role: "status" },
            () => icon(ds().status === "error" ? "errorCircle" : "bug", { size: 18, class: "mb-icon" }),
            h("div", { class: "mb-body inline" }, () => {
              const d = ds();
              if (d.status === "error") return [h("span", { class: "mb-title" }, `Couldn't start ${d.label} for debugging.`), h("span", { class: "mb-ellipsis" }, d.message ?? "")];
              if (d.status === "starting") return [h("span", { class: "mb-title" }, `Starting ${d.label} for debugging…`), h("span", { class: "mb-ellipsis" }, "Building and launching the test app paused")];
              if (d.status === "attached") return [h("span", { class: "mb-title" }, `Debugger attached (PID ${d.pid}) — running ${d.label}`)];
              const help = attachSteps(d, settings().editor);
              return [
                h("span", { class: "mb-title" }, "Waiting for debugger — attach to PID ", h("b", { class: "debug-pid" }, String(d.pid))),
                h(
                  "span",
                  { class: "mb-ellipsis", title: (["vscode", "rider", "visualstudio"] as const).map((x) => { const st = attachSteps(d, x); return `${st.name}: ${st.steps}`; }).join("\n") },
                  `${help.name}: ${help.steps}. ${d.label} runs once it is attached.`,
                ),
              ];
            }),
            show(
              () => ds().status === "waiting",
              () => copyButton({ text: () => String(debugSession.peek()?.pid ?? ""), label: "Copy PID" }),
            ),
            h(
              "button",
              { class: "btn btn-small mb-action", onclick: () => endDebugSession(true), title: () => (ds().status === "waiting" || ds().status === "attached" ? "Stop the debug run" : "Dismiss") },
              () => (ds().status === "waiting" || ds().status === "attached" ? "Stop" : "Dismiss"),
            ),
          ),
        ),
        reveal($.viewing, (viewing) =>
          h(
            "div",
            { class: () => cx("messagebar top", viewing().status === "missing" ? "mb-warning" : "mb-snapshot"), role: "status" },
            () => icon(viewing().status === "missing" ? "warning" : "history", { size: 18, class: "mb-icon" }),
            h("div", { class: "mb-body inline" }, () => {
              const v = viewing();
              if (v.status === "missing") {
                const when = v.run.startedAt ? ` (${formatRunTime(v.run.startedAt)})` : "";
                return h("span", { class: "mb-title" }, `That run is no longer stored${when}.`);
              }
              const counts = v.status === "loading" ? "loading…" : runCounts(v.run);
              const scope = v.run.scope === "subset" ? ` · ${plural(v.run.total, "selected test")}` : "";
              return [
                h("span", { class: "mb-title" }, `Viewing run from ${formatRunTime(v.run.startedAt)}`),
                h("span", { class: "mb-ellipsis" }, `(${counts}${scope}) — read-only snapshot`),
              ];
            }),
            show(
              () => viewing().status === "ready",
              () => h("button", { class: "btn btn-small mb-action", onclick: () => requestCompare(), title: "Compare this run with another stored run" }, "Compare with…"),
            ),
            h("button", { class: "btn btn-small mb-action", onclick: () => store.viewRun(undefined), title: "Back to the live results (Esc)" }, "Back to live"),
          ),
        ),
        reveal(
          () => errorProjects().length > 0,
          () =>
            h(
              "div",
              { class: "messagebar mb-error top", role: "alert" },
              icon("errorCircle", { size: 18, class: "mb-icon" }),
              h(
                "div",
                { class: "mb-body inline" },
                h("span", { class: "mb-title" }, () => {
                  const ep = errorProjects();
                  const n = buildErrorTotal();
                  const what = ep.length === 1 ? ep[0].name : `${ep.length} projects`;
                  const errors = n ? ` (${plural(n, "error")})` : "";
                  return `Build failed: ${what}${errors}.`;
                }),
                // the first error itself (not "Build failed with 2 error(s)" a second time)
                h("span", { class: "mb-ellipsis" }, () => {
                  if (errorProjects().some((p) => p.resultsOutdated)) return "Results shown are from the last successful build.";
                  const p = errorProjects().find((x) => x.buildErrors?.length);
                  const d = p?.buildErrors?.find((x) => x.severity === "error") ?? p?.buildErrors?.[0];
                  if (d) return diagnosticText(d);
                  const le = errorProjects()[0]?.lastError ?? "";
                  return /^Build failed\b/i.test(le) ? "" : le;
                }),
              ),
              h(
                "button",
                {
                  class: "btn btn-small mb-action",
                  title: "Rebuild and rediscover tests",
                  disabled: () => running() || busyProjects().length > 0 || !connected(),
                  onclick: () => rebuild(errorProjects.peek().map((p) => p.id)),
                },
                "Rebuild",
              ),
              h(
                "button",
                {
                  class: "btn btn-small mb-action",
                  onclick: () => {
                    const p = errorProjects.peek().find((x) => x.buildErrors?.length) ?? errorProjects.peek()[0];
                    if (p.buildErrors?.length) openBuildErrors(p.id);
                    else selectAndShow(`p:${p.id}`);
                  },
                },
                () => (errorProjects().some((x) => x.buildErrors?.length) ? "Show errors" : "View details"),
              ),
            ),
        ),
      ),

      h(
        "main",
        { class: "body-content", ref: (el) => (main = el) },
        h(
          "div",
          { class: "toolbar" },
          h(
            "div",
            { class: "toolbar-group" },
            // Run all / Stop share one slot (sized to the wider of the two) and crossfade: nothing to the right moves.
            h(
              "span",
              { class: "run-slot" },
              button(
                {
                  variant: "danger",
                  icon: "stopFilled",
                  class: () => !running() && "slot-hidden",
                  onclick: () => send({ type: "cancel" }),
                  title: `Stop the current run (${isMac ? "⌘" : "Ctrl+"}.)`,
                  "aria-hidden": () => !running(),
                  tabIndex: () => (running() ? undefined : -1),
                },
                " Stop",
              ),
              button(
                {
                  variant: "accent",
                  icon: "playFilled",
                  class: () => cx("run-primary", running() && "slot-hidden"),
                  onclick: runPrimary,
                  disabled: () => $.tests().size === 0 || !connected() || matching()?.length === 0,
                  title: () => (matching() ? `Run the ${matching()!.length} tests the filtered tree shows (clear the filter to run all)` : "Run all tests (Ctrl+Enter)"),
                  "aria-hidden": () => running(),
                  tabIndex: () => (running() ? -1 : undefined),
                },
                () => (matching() ? ` Run ${matching()!.length} matching` : " Run all"),
              ),
            ),
            button(
              {
                icon: "arrowClockwise",
                disabled: () => running() || failedCount() === 0 || !connected(),
                onclick: () => runTests(failedNow()),
                title: "Run the failed tests again",
              },
              " Run failed",
              // always rendered (neutral "0" when there are none) so the buttons after it never shift
              h("span", { class: () => cx("btn-count", "slot-count", failedCount() > 0 && "failed"), "aria-hidden": "true" }, () => failedCount()),
              h("span", { class: "visually-hidden" }, () => ` (${failedCount()})`),
            ),
            // Verify snapshots: "Accept snapshots (N)" while tests have pending snapshots (components/snapshot-view.ts)
            snapshotToolbarButton(() => running() || !connected()),
            // Run affected (git): split button + preview popover (components/run-affected.ts)
            h("lc-run-affected"),
            h("span", { class: "toolbar-sep" }),
            // Watch: a filled "Watching" toggle + a dropdown with the impact map (P1-8, P2-14).
            h(
              "span",
              { class: () => cx("split-btn", "watch-split", $.watch() && "on") },
              button(
                {
                  variant: "toggle",
                  pressed: () => $.watch(),
                  icon: () => ($.watch() ? "eyeFilled" : "eye"),
                  class: "watch-btn",
                  onclick: toggleWatch,
                  title: () => ($.watch() ? "Watching: affected tests re-run when files change (W to stop)" : "Watch: re-run affected tests when files change (W)"),
                  disabled: () => !connected(),
                },
                () => ($.watch() ? " Watching" : " Watch"),
              ),
              menuButton({
                label: "Watch options",
                icon: "chevronDown",
                iconOnly: true,
                size: "small",
                class: "btn split-chevron",
                disabled: () => !connected(),
                items: () => {
                  const im = $.impactMap();
                  return [
                    {
                      label: $.watch() ? "Stop watching" : "Start watching",
                      icon: $.watch() ? "eye" : "eyeFilled",
                      shortcut: "W",
                      onSelect: toggleWatch,
                    },
                    { separator: true },
                    {
                      label: im?.building ? "Building impact map…" : "Build impact map",
                      icon: "targetArrow",
                      description: im ? impactMapSummary(im) : "Run each test class once with coverage, so watch re-runs only the tests a change affects",
                      disabled: running() || busyProjects().length > 0 || !!im?.building,
                      onSelect: () => send({ type: "buildImpactMap" }),
                    },
                  ];
                },
              }),
            ),
            button(
              {
                variant: "toggle",
                pressed: () => $.coverageEnabled(),
                icon: () => ($.coverageEnabled() ? "shieldCheckFilled" : "shieldCheck"),
                onclick: () => {
                  const enabled = $.coverageEnabled.peek();
                  send({ type: "setCoverage", enabled: !enabled });
                  if (!enabled) view.set("coverage");
                },
                title: () => ($.coverageEnabled() ? "Collecting coverage: every run collects code coverage" : "Collect code coverage on every run"),
                disabled: () => !connected(),
              },
              " Collect coverage",
            ),
            button(
              {
                icon: "arrowSync",
                iconClass: () => busyProjects().length > 0 && "spin",
                onclick: () => send({ type: "discover" }),
                disabled: () => running() || busyProjects().length > 0 || !connected(),
                title: "Rebuild and rediscover tests",
                "aria-label": "Rediscover",
              },
              h("span", { class: "btn-text-collapse" }, " Rediscover"),
            ),
          ),
          runSummaryBar(suite, celebrating),
        ),
        suiteBar(suite),
        show($.watch, () => h("lc-watch-strip")),

        h(
          "div",
          { class: "workspace", style: { "--tree-w": () => `${treeW()}%` } },
          keyed(page, (p) => pageEnter(pageSlot(p))),
          show(logOpen, () => [
            h("div", {
              class: "splitter-h",
              onpointerdown: dragLog,
              role: "separator",
              "aria-orientation": "horizontal",
              "aria-label": "Resize output panel",
            }),
            h("div", { class: "log-wrap", style: { height: logH } }, logPanelSlot()),
          ], () => h("lc-log-strip", { onOpen: () => logOpen.set(true) })),
        ),
      ),
      // dialogs (rendered into body while open) + the run announcer
      commandPalette(() => {
        const sel = selected();
        const multi = multiSelection().size > 1;
        const acts = selectionActions(sel);
        return {
          runAll,
          runRandom: (seed) => {
            if (!runDisabled.peek() && $.tests.peek().size) send({ type: "run", order: "random", seed });
          },
          runFailed: () => runTests(failedNow()),
          runSelected: multi ? () => runTests(selectionTests(shownRoots.peek(), multiSelection.peek())) : acts.runSelected,
          selectedLabel: multi ? `${multiSelection().size} selected` : nodeLabel(sel),
          coverageSelected: acts.coverageSelected,
          toggleWatch,
          toggleCoverage: () => send({ type: "setCoverage", enabled: !$.coverageEnabled.peek() }),
          buildImpactMap: () => send({ type: "buildImpactMap" }),
          cancel: () => send({ type: "cancel" }),
          rediscover: () => send({ type: "discover" }),
          selectTest: (k: string) => {
            selectAndShow(k);
            requestAnimationFrame(() => focusTree());
          },
          viewRun: (id) => store.viewRun(id),
          page: (p) => {
            page.set(p);
            focusPage(p);
          },
          toggleOutput: () => logOpen.update((v) => !v),
          showShortcuts: () => shortcutsOpen.set(true),
          sort: (m: TreeSort) =>
            batch(() => {
              page.set("tests");
              treeSort.set(m);
            }),
          sortLabels: SORT_LABELS,
          currentSort: treeSort.peek(),
          runMatching: matching() ? { count: matching()!.length, run: () => runTests(matching.peek() ?? []) } : undefined,
          debugSelected: acts.debugSelected,
          restartHost: () => send({ type: "restartHost", projectId: sel && sel.kind !== "group" ? sel.projectId : undefined }),
          showBuildErrors: errorProjects().length ? openFirstBuildErrors : undefined,
          compareRuns: () => requestCompare(),
          copyLink,
        };
      }),
      shortcutsDialog(),
      buildErrorsDialog(),
      runAnnouncer(),
    ];

    function connectingPane() {
      return h(
        "section",
        { class: "detail-pane" },
        // No spinner here: the tree shows the one loading indicator (P2-2: no double spinners).
        h(
          "div",
          { class: "empty-state" },
          h("div", { class: "empty-title" }, "Connecting to the Labcoat host…"),
          h("div", { class: "empty-sub" }, () => {
            const r = $.retryInMs();
            if ($.conn() !== "reconnecting") return "Waiting for the first state snapshot.";
            const retry = r ? ` in ${Math.ceil(r / 1000)}s` : "";
            return `Host not reachable yet. Retrying${retry}.`;
          }),
        ),
      );
    }
  }
}

define("lc-app", LabcoatApp);

declare global {
  interface HTMLElementTagNameMap {
    "lc-app": LabcoatApp;
  }
}

/** addEventListener for the lifetime of the current owner. */
function listen<K extends keyof WindowEventMap>(target: Window, type: K, fn: (e: WindowEventMap[K]) => void, capture = false) {
  target.addEventListener(type, fn, capture);
  onCleanup(() => target.removeEventListener(type, fn, capture));
}

// ---- helpers (unchanged from App.tsx) ------------------------------------------------------------------

/**
 * Should the finished `run` be celebrated? "no" when it wasn't all green or the tests were already green before it;
 * "yes" for the first green run or the run that fixed them; "wait" while its history entries haven't arrived yet.
 */
function celebrationVerdict(run: RunSummary, tests: Map<string, TestNode>): "yes" | "no" | "wait" {
  const allPassed = run.failed === 0 && run.total >= 3 && run.passed > 0 && run.passed + run.skipped >= run.total;
  if (!allPassed) return "no";
  let inRun = 0;
  let allWereGreen = true;
  for (const t of tests.values()) {
    const h = t.history;
    if (!h) continue;
    const i = h.findIndex((e) => e.runId === run.runId);
    if (i < 0) continue;
    inRun++;
    const prev = i > 0 ? h[i - 1] : undefined;
    if (!prev || (prev.outcome !== "passed" && prev.outcome !== "skipped")) allWereGreen = false;
  }
  if (inRun === 0) return "wait";
  return allWereGreen ? "no" : "yes";
}

/** The live tests with outcome/duration/error replaced by those of a past run. */
function snapshotTests(tests: Map<string, TestNode>, details: RunDetails): Map<string, TestNode> {
  const byKey = new Map(details.results.map((r) => [`${r.projectId}|${r.id}`, r]));
  const out = new Map<string, TestNode>();
  for (const [k, t] of tests) {
    const r = byKey.get(k);
    const base = { ...t, errorType: undefined, stdOut: undefined, stdErr: undefined, stale: undefined, traceId: undefined };
    out.set(
      k,
      r
        ? { ...base, outcome: r.outcome, durationMs: r.durationMs, errorMessage: r.errorMessage, stackTrace: r.stackTrace, lastRunAt: details.run.finishedAt ?? details.run.startedAt, notInRun: undefined }
        : { ...base, outcome: "notRun", durationMs: undefined, errorMessage: undefined, stackTrace: undefined, lastRunAt: undefined, notInRun: true },
    );
  }
  return out;
}

// ---- deep links (F4) --------------------------------------------------------------------------------------

const FILTERS = new Set<StatusFilter>(["all", "failed", "new", "passed", "skipped", "notRun", "flaky"]);
const SORTS: TreeSort[] = ["default", "slowest", "fastest"];

/** `#/tests?t=<projectId>|<testId>&f=failed&run=<runId>` (n=<node key> for non-test nodes). */
function parseHash(): { page?: Page; key?: string; filter?: StatusFilter; run?: string; sort?: TreeSort } {
  const m = /^#\/(tests|coverage|settings)\b(?:\?(.*))?$/.exec(location.hash);
  if (!m) return {};
  const q = new URLSearchParams(m[2] ?? "");
  const t = q.get("t");
  const n = q.get("n");
  const f = q.get("f") as StatusFilter | null;
  return {
    page: m[1] as Page,
    key: t ? `t:${t}` : (n ?? undefined),
    filter: f && FILTERS.has(f) ? f : undefined,
    run: q.get("run") ?? undefined,
    sort: SORTS.find((x) => x === q.get("sort")),
  };
}

function buildHash(page: Page, key: string | undefined, filter: StatusFilter, run: string | undefined, sort: TreeSort): string {
  const q = new URLSearchParams();
  if (page === "tests" && key) key.startsWith("t:") ? q.set("t", key.slice(2)) : q.set("n", key);
  if (page === "tests" && filter !== "all") q.set("f", filter);
  if (page === "tests" && sort !== "default") q.set("sort", sort);
  if (run) q.set("run", run);
  const qs = q.toString().replace(/%7C/gi, "|");
  const query = qs ? `?${qs}` : "";
  return `#/${page}${query}`;
}

/** "1 test", "2 tests". */
function plural(n: number, noun: string) {
  return `${n} ${noun}${n === 1 ? "" : "s"}`;
}

/** A build diagnostic in one line: "File.cs:12 CS0103: message". */
function diagnosticText(d: BuildDiagnostic) {
  let at = "";
  if (d.file) {
    const line = d.line ? `:${d.line}` : "";
    at = `${d.file.split(/[\\/]/).pop()}${line} `;
  }
  const code = d.code ? `${d.code}: ` : "";
  return `${at}${code}${d.message}`;
}

function impactMapSummary(im: ImpactMapInfo) {
  const built = im.builtAt ? ` · built ${new Date(im.builtAt).toLocaleTimeString()}` : "";
  return `${im.files} source files → ${im.tests} tests${built}`;
}

/** The palette's label for a node: the test's name, else the node's label. */
function nodeLabel(n: TreeNode | undefined) {
  if (!n) return undefined;
  return n.test ? n.test.displayName : n.label;
}

/** The node's project failed to build: nothing in it can run. */
function isBuildBlocked(n: TreeNode) {
  return n.project?.status === "error" || projectOfNode(n)?.status === "error";
}

function projectOfNode(n: TreeNode) {
  let p: TreeNode | undefined = n;
  while (p && !p.project) p = p.parent;
  return p?.project;
}


const KIND_TITLES: Partial<Record<TreeNode["kind"], string>> = { class: "Class", namespace: "Namespace" };

/** What "coverage for this node" means: its test leaves, labelled like the double-click banner. */
export function coverageTargetFor(n: TreeNode): CoverageTarget | undefined {
  const list = leaves(n);
  if (!list.length) return undefined;
  const pids = new Set(list.map((t) => t.projectId));
  const t = n.test;
  const label: CoverageLabel = t
    ? { title: t.displayName, context: [t.namespace, t.className].filter(Boolean).join(".") }
    : {
        title: n.label,
        context: `${KIND_TITLES[n.kind] ?? "Method"} · ${plural(list.length, "test")}`,
      };
  return { key: n.key, label, testIds: list.map((x) => x.id), projectId: pids.size === 1 ? list[0].projectId : undefined, isTest: !!t };
}

function drag(e: PointerEvent, move: (ev: PointerEvent) => void) {
  e.preventDefault();
  const target = e.currentTarget as HTMLElement;
  target.classList.add("dragging");
  document.body.classList.add("is-dragging");
  const up = () => {
    target.classList.remove("dragging");
    document.body.classList.remove("is-dragging");
    window.removeEventListener("pointermove", move);
    window.removeEventListener("pointerup", up);
  };
  window.addEventListener("pointermove", move);
  window.addEventListener("pointerup", up);
}
