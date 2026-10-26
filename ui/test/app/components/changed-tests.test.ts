import { beforeEach, describe, expect, it, vi } from "vitest";
import { changedAvailable, changedKeys, refreshChanged, setChangedTests, watchChanged } from "../../../src/components/changed-tests";
import { filterText, statusFilter } from "../../../src/components/tree-pane";
import type { ChangedTests } from "../../../src/contract";
import { serverEventListeners } from "../../../src/store";
import { flushFrame, makeStore, testNode, text } from "../../helpers/app";
import { flushMicrotasks } from "../../helpers/dom";
import { mountTree, resetTreeState } from "../../helpers/tree-pane";

const answer = (...ids: string[]): ChangedTests => ({ available: true, base: "branch", ref: "origin/main", tests: ids.map((id) => ({ projectId: "P1", id })) });
type Fetch = { fetchChangedTests?: (signal?: AbortSignal) => Promise<ChangedTests> };

beforeEach(() => {
  setChangedTests({ available: false, tests: [] });
});

describe("changedKeys", () => {
  it("holds the testKeys of an available answer and is empty outside a repository", () => {
    expect(changedAvailable()).toBe(false);
    setChangedTests(answer("N.C.A", "N.C.B"));
    expect(changedAvailable()).toBe(true);
    expect([...changedKeys()]).toEqual(["P1|N.C.A", "P1|N.C.B"]);
    setChangedTests({ available: false, error: "Not a git repository", tests: [{ projectId: "P1", id: "N.C.A" }] });
    expect(changedAvailable()).toBe(false);
    expect(changedKeys().size).toBe(0);
  });

  it("keeps the same set for an equal answer (filter passes are cached by it)", () => {
    setChangedTests(answer("N.C.A"));
    const before = changedKeys();
    setChangedTests(answer("N.C.A"));
    expect(changedKeys()).toBe(before);
    setChangedTests(answer("N.C.A", "N.C.B"));
    expect(changedKeys()).not.toBe(before);
  });
});

describe("refreshing", () => {
  it("reads the transport; a failure keeps the last answer", async () => {
    const { transport } = makeStore({ state: { tests: [testNode({ id: "N.C.A" })] } });
    const t = transport as unknown as Fetch;
    t.fetchChangedTests = vi.fn(async () => answer("N.C.A"));
    await refreshChanged();
    expect([...changedKeys()]).toEqual(["P1|N.C.A"]);
    t.fetchChangedTests = vi.fn(async () => {
      throw new Error("offline");
    });
    await refreshChanged();
    expect([...changedKeys()]).toEqual(["P1|N.C.A"]);
  });

  it("watchChanged refreshes on mount, on focus, after a run and after a discovery, until stopped", async () => {
    const { transport } = makeStore({ state: { tests: [testNode({ id: "N.C.A" })] } });
    const fetch = vi.fn(async () => answer("N.C.A"));
    (transport as unknown as Fetch).fetchChangedTests = fetch;
    const stop = watchChanged();
    await flushMicrotasks();
    expect(fetch).toHaveBeenCalledTimes(1);
    window.dispatchEvent(new FocusEvent("focus"));
    await flushMicrotasks();
    expect(fetch).toHaveBeenCalledTimes(2);
    for (const type of ["runFinished", "state"]) for (const l of serverEventListeners) l({ type });
    await flushMicrotasks();
    expect(fetch).toHaveBeenCalledTimes(4);
    for (const l of serverEventListeners) l({ type: "log" });
    stop();
    window.dispatchEvent(new FocusEvent("focus"));
    await flushMicrotasks();
    expect(fetch).toHaveBeenCalledTimes(4);
  });
});

describe("Changed chip and is:changed", () => {
  const chipText = (el: Element) => [...el.querySelectorAll<HTMLButtonElement>(".chips .chip")].map((c) => text(c));

  it("shows the chip once tests are changed and filters the tree by the chip and the token", async () => {
    resetTreeState();
    const tests = [testNode({ id: "N.C.Old", outcome: "passed" }), testNode({ id: "N.C.Edited", outcome: "passed" })];
    const { transport } = makeStore({ state: { tests } });
    (transport as unknown as Fetch).fetchChangedTests = vi.fn(async () => answer("N.C.Edited"));
    const t = mountTree({ tests });
    await flushFrame();
    await flushMicrotasks();
    expect(chipText(t.el)).toEqual(["All2", "Failed0", "Changed1", "Passed2"]);
    statusFilter.set("changed");
    expect(t.labels()).toEqual(["Edited"]);
    statusFilter.set("all");
    filterText.set("is:changed");
    expect(t.labels()).toEqual(["Project.One", "N", "C", "Edited"]);
    filterText.set("is:changed old");
    expect(t.labels()).toEqual([]);
    filterText.set("");
    t.dispose();
  });
});
