import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { h } from "@eriklieben/labcoat-design-system";
import {
  AffectedError,
  affectedBase,
  affectedCommands,
  affectedPreviewOpen,
  affectedRef,
  affectedRunLabel,
  baseLabel,
  branchBaseName,
  fetchAffectedPlan,
  fetchGitInfo,
  gitInfo,
  gitSummary,
  noBranchBase,
  openAffectedPreview,
  planHeadline,
  refreshGit,
  runAffected,
  setGitInfo,
  setAffectedBase,
  validRef,
} from "../../../src/components/run-affected";
import type { AffectedBase, AffectedPlan, GitInfo } from "../../../src/contract";
import { serverEventListeners } from "../../../src/store";
import { click, flushFrame, key, makeStore, mount, runSummary, testNode, text, type } from "../../helpers/app";
import { flushMicrotasks } from "../../helpers/dom";

const GIT: GitInfo = { available: true, root: "/repo", branch: "feature/x", head: "abc1234", dirty: 3, defaultBase: "origin/main", ahead: 2, behind: 1 };

function plan(p: Partial<AffectedPlan> = {}): AffectedPlan {
  return {
    base: "working",
    git: GIT,
    files: [
      { path: "/repo/src/Orders.cs", relativePath: "src/Orders.cs", status: "modified", kind: "product", mapping: "impactMap", tests: 1, projectIds: ["P1"], lines: [[3, 5]], methods: ["Validate"] },
      { path: "/repo/src/New.cs", relativePath: "src/New.cs", oldPath: "src/Old.cs", status: "renamed", kind: "product", mapping: "project", tests: 2 },
      { path: "/repo/README.md", relativePath: "README.md", status: "untracked", kind: "other", mapping: "none", tests: 0 },
    ],
    tests: [
      { projectId: "P1", id: "Ns.ClassA.Test1", reasons: ["Orders.cs changed (method Validate)"] },
      { projectId: "P1", id: "Ns.ClassA.Test2", reasons: ["no impact data for New.cs: all tests of P1"] },
    ],
    projects: [{ projectId: "P1", name: "Project.One", tests: 2, all: true }],
    counts: { files: 3, tests: 2, projects: 1 },
    strategy: "mixed",
    ...p,
  };
}

type Fetchers = {
  fetchGitInfo?: (signal?: AbortSignal) => Promise<GitInfo>;
  fetchAffectedPlan?: (base: AffectedBase, ref: string | undefined, signal?: AbortSignal) => Promise<AffectedPlan>;
};

beforeEach(() => {
  affectedBase.set("working");
  affectedRef.set("");
  affectedPreviewOpen.set(false);
  gitInfo.set(undefined);
});
afterEach(() => {
  affectedPreviewOpen.set(false);
});

async function settle() {
  await flushMicrotasks();
  await flushFrame();
}

function setup(opts: { git?: GitInfo; plan?: (base: AffectedBase, ref: string | undefined, signal?: AbortSignal) => Promise<AffectedPlan>; running?: boolean } = {}) {
  const ctx = makeStore({
    state: {
      tests: [testNode({ id: "Ns.ClassA.Test1", displayName: "Test one" }), testNode({ id: "Ns.ClassA.Test2" })],
      currentRun: opts.running ? runSummary() : undefined,
    },
  });
  const t = ctx.transport as unknown as Fetchers;
  t.fetchGitInfo = vi.fn(async () => opts.git ?? GIT);
  t.fetchAffectedPlan = vi.fn(opts.plan ?? (async (base) => plan({ base })));
  return { ...ctx, fetchers: t as Required<Fetchers> };
}

const popup = () => document.querySelector<HTMLElement>(".affected-popover");
const mainBtn = () => document.querySelector<HTMLButtonElement>(".affected-btn")!;

describe("helpers", () => {
  it("validates refs like the host", () => {
    expect(validRef("main..HEAD")).toBe(true);
    expect(validRef("HEAD~3...origin/main")).toBe(true);
    expect(validRef("@{u}")).toBe(true);
    expect(validRef("-x")).toBe(false);
    expect(validRef("a b")).toBe(false);
    expect(validRef("a;b")).toBe(false);
    expect(validRef("")).toBe(false);
    expect(validRef(undefined)).toBe(false);
    expect(validRef("a".repeat(201))).toBe(false);
  });

  it("labels bases and runs", () => {
    expect(baseLabel("working", GIT)).toBe("Uncommitted changes");
    expect(baseLabel("branch", GIT)).toBe("Branch vs origin/main");
    expect(baseLabel("branch", GIT, "develop")).toBe("Branch vs develop");
    expect(baseLabel("range", GIT)).toBe("Commit range…");
    expect(baseLabel("range", GIT, "a..b")).toBe("Commit range a..b");
    expect(affectedRunLabel(undefined)).toBeUndefined();
    expect(affectedRunLabel(runSummary({ affected: { strategy: "testFile", changedFiles: [] } }))).toBeUndefined();
    expect(affectedRunLabel(runSummary({ affected: { strategy: "git", changedFiles: [] } }))).toBe("Affected (uncommitted changes)");
    expect(affectedRunLabel(runSummary({ affected: { strategy: "git", base: "branch", ref: "origin/main", changedFiles: [] } }))).toBe("Affected (branch vs origin/main)");
    expect(affectedRunLabel(runSummary({ affected: { strategy: "git", base: "branch", changedFiles: [] } }))).toBe("Affected (branch vs base)");
    expect(affectedRunLabel(runSummary({ affected: { strategy: "git", base: "range", ref: "a..b", changedFiles: [] } }))).toBe("Affected (range a..b)");
    expect(affectedRunLabel(runSummary({ affected: { strategy: "git", base: "range", changedFiles: [] } }))).toBe("Affected (commit range)");
  });

  it("handles a repository without commits or default base", () => {
    const fresh: GitInfo = { available: true, root: "/repo", branch: "main" };
    expect(branchBaseName(undefined)).toBe("main");
    expect(branchBaseName(fresh)).toBe("base");
    expect(noBranchBase(fresh)).toBe(true);
    expect(noBranchBase(GIT)).toBe(false);
    expect(noBranchBase({ available: false })).toBe(false);
    expect(gitSummary(fresh)).toBe("main");
    expect(gitSummary({ available: true })).toBe("no commits");
    expect(gitSummary({ available: true, head: "abc", detached: true, dirty: 2 })).toBe("abc (detached) · 2");
    expect(gitSummary({ available: false })).toBeUndefined();
    expect(gitSummary(undefined)).toBeUndefined();
  });

  it("headlines", () => {
    expect(planHeadline(plan())).toBe("2 tests in 1 project · 3 files changed");
    expect(planHeadline(plan({ counts: { files: 1, tests: 0, projects: 0 } }))).toBe("No affected tests · 1 file changed");
    expect(planHeadline(plan({ counts: { files: 0, tests: 0, projects: 0 } }))).toBe("No changed files");
  });

  it("remembers the base; storage failures are ignored", () => {
    setAffectedBase("range", "a..b");
    expect(localStorage.getItem("mste.affectedBase")).toBe('"range"');
    expect(localStorage.getItem("mste.affectedRef")).toBe('"a..b"');
    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("quota");
    });
    expect(() => setAffectedBase("branch")).not.toThrow();
    expect(affectedBase.peek()).toBe("branch");
    spy.mockRestore();
  });
});

describe("REST", () => {
  it("GET /api/git and /api/affected (ws transport)", async () => {
    makeStore({ kind: "ws" });
    const fetchMock = vi.fn(async (url: URL) => {
      if (url.pathname === "/api/git") return new Response(JSON.stringify(GIT), { status: 200 });
      if (url.searchParams.get("ref") === "nope") return new Response(JSON.stringify({ error: "Unknown revision 'nope'.", code: "unknownRef" }), { status: 404 });
      if (url.searchParams.get("base") === "branch") return new Response("oops", { status: 500, statusText: "Server Error" });
      return new Response(JSON.stringify(plan({ base: url.searchParams.get("base") as AffectedBase })), { status: 200 });
    });
    vi.stubGlobal("fetch", fetchMock);
    await expect(fetchGitInfo()).resolves.toEqual(GIT);
    const p = await fetchAffectedPlan("range", "a..b");
    expect(p.base).toBe("range");
    const url = fetchMock.mock.calls[1][0] as URL;
    expect(url.pathname).toBe("/api/affected");
    expect(url.searchParams.get("ref")).toBe("a..b");
    await expect(fetchAffectedPlan("range", "nope")).rejects.toMatchObject({ message: "Unknown revision 'nope'.", status: 404, code: "unknownRef" });
    const err = await fetchAffectedPlan("branch", undefined).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(AffectedError);
    expect(err).toMatchObject({ status: 500, message: "500 Server Error" });
    expect((fetchMock.mock.calls[3][0] as URL).searchParams.has("ref")).toBe(false);
  });

  it("refreshGit keeps the last value on errors and ignores aborted answers", async () => {
    const { fetchers } = setup();
    await refreshGit();
    expect(gitInfo.peek()).toEqual(GIT);
    (fetchers.fetchGitInfo as ReturnType<typeof vi.fn>).mockRejectedValueOnce(new Error("offline"));
    await refreshGit();
    expect(gitInfo.peek()).toEqual(GIT);
    // a newer refresh supersedes an older one
    let release!: (g: GitInfo) => void;
    (fetchers.fetchGitInfo as ReturnType<typeof vi.fn>).mockImplementationOnce(() => new Promise<GitInfo>((r) => (release = r)));
    const first = refreshGit();
    const second = refreshGit();
    release({ available: false });
    await first;
    await second;
    expect(gitInfo.peek()).toEqual(GIT);
  });
});

describe("setGitInfo", () => {
  it("keeps the same object for an equal answer (no re-render of the open menu)", () => {
    setGitInfo({ ...GIT });
    const first = gitInfo.peek();
    setGitInfo({ ...GIT });
    expect(gitInfo.peek()).toBe(first);
    setGitInfo({ ...GIT, dirty: 4 });
    expect(gitInfo.peek()).not.toBe(first);
    expect(gitInfo.peek()!.dirty).toBe(4);
  });
});

describe("runAffected", () => {
  it("sends the command with the last-used base", () => {
    const { transport } = setup();
    expect(runAffected()).toBe(true);
    expect(transport.sent.at(-1)).toEqual({ type: "runAffected", base: "working", ref: undefined, coverage: undefined, buildImpactMap: undefined });
    runAffected({ base: "branch", coverage: true, buildImpactMap: true });
    expect(transport.sent.at(-1)).toEqual({ type: "runAffected", base: "branch", ref: undefined, coverage: true, buildImpactMap: true });
    expect(affectedBase.peek()).toBe("branch");
    runAffected({ base: "range", ref: "HEAD~2..HEAD" });
    expect(transport.sent.at(-1)).toMatchObject({ base: "range", ref: "HEAD~2..HEAD" });
    expect(affectedRef.peek()).toBe("HEAD~2..HEAD");
  });

  it("a range without a valid ref opens the preview instead", () => {
    const { transport } = setup();
    expect(runAffected({ base: "range", ref: "-bad" })).toBe(false);
    expect(transport.sent).toEqual([]);
    expect(affectedPreviewOpen.peek()).toBe(true);
    expect(affectedBase.peek()).toBe("range");
  });

  it("palette commands", () => {
    const { transport } = setup();
    gitInfo.set(GIT);
    const cmds = affectedCommands(false);
    expect(cmds.map((c) => c.label)).toEqual(["Run affected: uncommitted changes", "Run affected: branch vs origin/main", "Preview affected tests…"]);
    expect(cmds[0].detail).toBe("3 changed files");
    expect(cmds[1].detail).toBe("feature/x");
    cmds[1].run();
    expect(transport.sent.at(-1)).toMatchObject({ type: "runAffected", base: "branch" });
    cmds[0].run();
    expect(transport.sent.at(-1)).toMatchObject({ base: "working" });
    cmds[2].run();
    expect(affectedPreviewOpen.peek()).toBe(true);
    expect(affectedCommands(true).slice(0, 2).every((c) => c.disabled)).toBe(true);
    gitInfo.set({ available: false, error: "no repo" });
    expect(affectedCommands(false)[0]).toMatchObject({ disabled: true, detail: "not a git repository" });
    gitInfo.set({ available: true, branch: "main" });
    expect(affectedCommands(false)[1]).toMatchObject({ disabled: true, label: "Run affected: branch vs base", detail: "no base branch found (set affectedBase)" });
    gitInfo.set(undefined);
    expect(affectedCommands(false)[0].detail).toBeUndefined();
  });
});

describe("<lc-run-affected>", () => {
  it("shows the git state and runs with the last-used base", async () => {
    const { transport } = setup();
    mount(h("lc-run-affected"));
    await settle();
    const btn = mainBtn();
    expect(text(btn)).toBe("Run affectedfeature/x · 3 (feature/x, 3 uncommitted)");
    expect(btn.title).toContain("uncommitted changes");
    click(btn);
    expect(transport.sent.at(-1)).toMatchObject({ type: "runAffected", base: "working" });
    affectedBase.set("branch");
    await settle();
    expect(btn.title).toContain("vs origin/main");
    affectedBase.set("range");
    await settle();
    expect(btn.title).toContain("a commit range");
    affectedRef.set("a..b");
    await settle();
    expect(btn.title).toContain("the commits a..b");
  });

  it("a clean tree shows only the branch", async () => {
    setup({ git: { ...GIT, dirty: 0 } });
    mount(h("lc-run-affected"));
    await settle();
    expect(text(mainBtn())).toBe("Run affectedfeature/x (feature/x)");
  });

  it("is disabled while running or offline", async () => {
    setup({ running: true });
    mount(h("lc-run-affected"));
    await settle();
    expect(mainBtn().disabled).toBe(true);
  });

  it("without git: no chip, an explaining tooltip, and the click opens the preview with the error", async () => {
    setup({
      git: { available: false, error: "not a git repository" },
      plan: async () => {
        throw new AffectedError("The workspace is not in a git repository.", 409, "notGitRepo");
      },
    });
    mount(h("lc-run-affected"));
    await settle();
    expect(document.querySelector(".affected-git")).toBeNull();
    expect(mainBtn().title).toBe("Run affected needs a git repository: not a git repository");
    click(mainBtn());
    await settle();
    await settle();
    expect(popup()).not.toBeNull();
    expect(text(popup()!.querySelector(".ap-status"))).toBe("The workspace is not in a git repository.");
    expect(popup()!.querySelector<HTMLButtonElement>(".ap-run")!.disabled).toBe(true);
  });

  it("refreshes git on focus and after a run", async () => {
    const { fetchers } = setup();
    mount(h("lc-run-affected"));
    await settle();
    const calls = () => (fetchers.fetchGitInfo as ReturnType<typeof vi.fn>).mock.calls.length;
    const n = calls();
    window.dispatchEvent(new Event("focus"));
    for (const l of serverEventListeners) l({ type: "runFinished" });
    for (const l of serverEventListeners) l({ type: "log" });
    await settle();
    expect(calls()).toBe(n + 2);
  });

  it("menu: picking a base opens the preview for it", async () => {
    setup();
    mount(h("lc-run-affected"));
    await settle();
    const chevron = document.querySelector<HTMLButtonElement>(".affected-split .split-chevron")!;
    chevron.focus();
    click(chevron);
    await settle();
    const items = [...document.querySelectorAll<HTMLButtonElement>(".lc-menu-item")];
    expect(items.map((i) => i.dataset.text)).toEqual(["Uncommitted changes", "Branch", "Commit range…", "Preview affected tests…"]);
    expect(text(items[0])).toContain("3 changed files");
    expect(text(items[1])).toContain("feature/x since it left origin/main");
    click(items[1]);
    await settle();
    expect(affectedBase.peek()).toBe("branch");
    expect(affectedPreviewOpen.peek()).toBe(true);
  });

  it("menu descriptions without git info / clean tree / no default base", async () => {
    setup({ git: { available: true, branch: "main", dirty: 0 } });
    mount(h("lc-run-affected"));
    await settle();
    click(document.querySelector<HTMLButtonElement>(".affected-split .split-chevron")!);
    await settle();
    const items = [...document.querySelectorAll<HTMLButtonElement>(".lc-menu-item")];
    expect(text(items[0])).toContain("working tree is clean");
    expect(text(items[1])).toContain("Branch vs base");
    expect(text(items[1])).toContain("no base branch found");
    expect(text(items[2])).toContain("e.g. HEAD~3..HEAD");
    click(items[3]);
    await settle();
    expect(affectedPreviewOpen.peek()).toBe(true);
  });

  it("Mod+Shift+Enter opens the preview (not inside a dialog, not without shift)", async () => {
    setup();
    mount(h("lc-run-affected"));
    await settle();
    key(window, "Enter", { ctrlKey: true });
    expect(affectedPreviewOpen.peek()).toBe(false);
    const dlg = document.createElement("dialog");
    dlg.setAttribute("open", "");
    document.body.append(dlg);
    key(window, "Enter", { ctrlKey: true, shiftKey: true });
    expect(affectedPreviewOpen.peek()).toBe(false);
    dlg.remove();
    const e = key(window, "Enter", { metaKey: true, shiftKey: true });
    expect(e.defaultPrevented).toBe(true);
    expect(affectedPreviewOpen.peek()).toBe(true);
  });
});

describe("<lc-affected-preview> (in the popover)", () => {
  async function open(opts: Parameters<typeof setup>[0] = {}) {
    const ctx = setup(opts);
    mount(h("lc-run-affected"));
    await settle();
    openAffectedPreview();
    await settle();
    await settle();
    return ctx;
  }

  it("lists files and tests with reasons, then runs", async () => {
    const { transport, fetchers } = await open();
    const p = popup()!;
    expect(fetchers.fetchAffectedPlan).toHaveBeenCalledWith("working", undefined, expect.any(AbortSignal));
    expect(text(p.querySelector(".ap-status"))).toBe("2 tests in 1 project · 3 files changed");
    expect(p.querySelector(".ap-status")!.getAttribute("aria-live")).toBe("polite");
    expect(text(p.querySelector(".ap-git"))).toBe("feature/x · 3 uncommitted · ↑2 ↓1");
    const files = [...p.querySelectorAll(".ap-file")].map((li) => [...li.children].map(text));
    expect(files[0]).toEqual(["M", "modified:", "src/Orders.cs", "impact map · Validate", "1 test"]);
    expect(files[1]).toEqual(["R", "renamed:", "src/Old.cs → src/New.cs", "whole project", "2 tests"]);
    expect(files[2]).toEqual(["U", "untracked:", "README.md", "no tests", ""]);
    expect(p.querySelector(".ap-st")!.getAttribute("aria-hidden")).toBe("true");
    expect(p.querySelector(".ap-file:nth-child(3)")!.classList.contains("dim")).toBe(true);
    expect(text(p.querySelector(".ap-project-name"))).toBe("Project.One 2 tests (all)");
    const tests = [...p.querySelectorAll(".ap-test")].map(text);
    expect(tests).toEqual(["Test one: Orders.cs changed (method Validate)", "Test2: no impact data for New.cs: all tests of P1"]);
    expect(text(p.querySelector(".ap-run"))).toBe("Run 2 tests");
    // coverage option
    const cov = p.querySelector<HTMLInputElement>(".ap-check input")!;
    cov.checked = true;
    cov.dispatchEvent(new Event("change"));
    click(p.querySelector<HTMLButtonElement>(".ap-run")!);
    expect(transport.sent.at(-1)).toEqual({ type: "runAffected", base: "working", ref: undefined, coverage: true, buildImpactMap: undefined });
    expect(affectedPreviewOpen.peek()).toBe(false);
  });

  it("impact map: build it now, or first; refetches when the map changes", async () => {
    const { transport, store, fetchers } = await open({ plan: async (base) => plan({ base, needsImpactMap: true, warnings: ["shallow clone"] }) });
    const p = popup()!;
    expect(text(p.querySelector(".ap-warnings"))).toBe("shallow clone");
    const build = p.querySelector<HTMLButtonElement>(".ap-impact button")!;
    expect(text(build)).toBe("Build impact map");
    click(build);
    expect(transport.sent.at(-1)).toEqual({ type: "buildImpactMap" });
    const n = (fetchers.fetchAffectedPlan as ReturnType<typeof vi.fn>).mock.calls.length;
    store.apply({ type: "settingsChanged", settings: { watch: false, coverageEnabled: false, tracingEnabled: false, impactMap: { files: 0, tests: 0, building: true } } });
    await settle();
    expect(text(popup()!.querySelector(".ap-impact button"))).toBe("Building impact map…");
    expect(popup()!.querySelector<HTMLButtonElement>(".ap-impact button")!.disabled).toBe(true);
    store.apply({ type: "settingsChanged", settings: { watch: false, coverageEnabled: false, tracingEnabled: false, impactMap: { files: 4, tests: 9 } } });
    await settle();
    await settle();
    expect((fetchers.fetchAffectedPlan as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(n);
    const checks = popup()!.querySelectorAll<HTMLInputElement>(".ap-check input");
    expect(checks).toHaveLength(2);
    checks[1].checked = true;
    checks[1].dispatchEvent(new Event("change"));
    click(popup()!.querySelector<HTMLButtonElement>(".ap-run")!);
    expect(transport.sent.at(-1)).toMatchObject({ type: "runAffected", buildImpactMap: true, coverage: undefined });
  });

  it("switching the base refetches and aborts the stale request", async () => {
    const signals: AbortSignal[] = [];
    const { fetchers } = await open({
      plan: (base, _ref, signal) => {
        signals.push(signal!);
        return base === "working" ? Promise.resolve(plan({ base })) : new Promise(() => {});
      },
    });
    const radios = popup()!.querySelectorAll<HTMLInputElement>(".ap-base input");
    expect([...radios].map((r) => r.parentElement!.textContent)).toEqual(["Uncommitted changes", "Branch vs origin/main", "Commit range"]);
    radios[1].checked = true;
    radios[1].dispatchEvent(new Event("change"));
    await settle();
    expect(affectedBase.peek()).toBe("branch");
    expect(fetchers.fetchAffectedPlan).toHaveBeenLastCalledWith("branch", undefined, expect.any(AbortSignal));
    // the previous plan stays visible while loading
    expect(text(popup()!.querySelector(".ap-status"))).toBe("2 tests in 1 project · 3 files changed");
    expect(popup()!.querySelector(".ap-status")!.classList.contains("loading")).toBe(true);
    radios[0].checked = true;
    radios[0].dispatchEvent(new Event("change"));
    await settle();
    expect(signals[1].aborted).toBe(true);
  });

  it("range: validates the ref, debounces, Enter runs; errors are shown", async () => {
    vi.useFakeTimers();
    const { transport, fetchers } = setup({
      plan: async (base, ref) => {
        if (ref === "nope") throw new AffectedError("Unknown revision 'nope'.", 404, "unknownRef");
        return plan({ base, ref, mergeBase: undefined });
      },
    });
    affectedBase.set("range");
    mount(h("lc-run-affected"));
    await settle();
    openAffectedPreview();
    await settle();
    const p = popup()!;
    expect(text(p.querySelector(".ap-status"))).toBe("Enter a commit range, e.g. main..HEAD or HEAD~3..HEAD");
    const input = p.querySelector<HTMLInputElement>(".ap-ref")!;
    type(input, "-x");
    await settle();
    expect(input.getAttribute("aria-invalid")).toBe("true");
    expect(text(p.querySelector(".ap-hint"))).toContain("must not start with -");
    type(input, "nope");
    await settle();
    vi.advanceTimersByTime(400);
    await settle();
    expect(text(p.querySelector(".ap-status"))).toBe("Unknown revision 'nope'.");
    expect(p.querySelector(".ap-status")!.classList.contains("error")).toBe(true);
    type(input, "main..HEAD");
    await settle();
    expect(fetchers.fetchAffectedPlan).not.toHaveBeenLastCalledWith("range", "main..HEAD", expect.anything());
    vi.advanceTimersByTime(400);
    await settle();
    expect(fetchers.fetchAffectedPlan).toHaveBeenLastCalledWith("range", "main..HEAD", expect.any(AbortSignal));
    expect(text(p.querySelector(".ap-ref-used"))).toBe("main..HEAD");
    key(input, "Enter");
    expect(transport.sent.at(-1)).toMatchObject({ type: "runAffected", base: "range", ref: "main..HEAD" });
    expect(affectedRef.peek()).toBe("main..HEAD");
  });

  it("branch: shows the merge base; nothing affected disables Run", async () => {
    affectedBase.set("branch");
    await open({ plan: async (base) => plan({ base, ref: "origin/main", mergeBase: "9e1d04c", tests: [], projects: [], counts: { files: 3, tests: 0, projects: 0 } }) });
    const p = popup()!;
    expect(text(p.querySelector(".ap-status"))).toBe("No affected tests · 3 files changedmerge-base 9e1d04c");
    expect(p.querySelector(".ap-tests")).toBeNull();
    expect(p.querySelector<HTMLButtonElement>(".ap-run")!.disabled).toBe(true);
    expect(text(p.querySelector(".ap-run"))).toBe("Run");
  });

  it("caps long test lists; refresh refetches; Cancel and Escape close", async () => {
    const many = Array.from({ length: 205 }, (_, i) => ({ projectId: "P1", id: `T${i}`, reasons: ["x"] }));
    const { fetchers } = await open({ plan: async (base) => plan({ base, tests: many, counts: { files: 3, tests: 205, projects: 1 } }) });
    expect(text(popup()!.querySelector(".ap-more"))).toBe("+5 more");
    const n = (fetchers.fetchAffectedPlan as ReturnType<typeof vi.fn>).mock.calls.length;
    const refresh = [...popup()!.querySelectorAll<HTMLButtonElement>(".ap-foot button")].find((b) => text(b) === "Refresh")!;
    click(refresh);
    await settle();
    expect((fetchers.fetchAffectedPlan as ReturnType<typeof vi.fn>).mock.calls).toHaveLength(n + 1);
    const cancel = [...popup()!.querySelectorAll<HTMLButtonElement>(".ap-foot button")].find((b) => text(b) === "Cancel")!;
    click(cancel);
    await settle();
    expect(affectedPreviewOpen.peek()).toBe(false);
    openAffectedPreview();
    await settle();
    key(popup()!.querySelector(".ap-base input")!, "Escape");
    await settle();
    expect(affectedPreviewOpen.peek()).toBe(false);
  });

  it("non-Error rejections and ignored aborts", async () => {
    await open({
      plan: async () => {
        throw "boom";
      },
    });
    expect(text(popup()!.querySelector(".ap-status"))).toBe("boom");
  });

  it("an AbortError rejection leaves the loading state", async () => {
    await open({
      plan: async () => {
        throw new DOMException("Aborted", "AbortError");
      },
    });
    expect(text(popup()!.querySelector(".ap-status"))).toBe("Finding affected tests…");
  });
});
