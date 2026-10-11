/**
 * Store (src/store.ts): applying server events to a 20,000-test state, one "frame" per sample (apply + the
 * once-per-frame publish to the signals). `testsUpdated` carries full entries, `testResults` (Round 7) results-only
 * patches; both are what the host streams during a run.
 */
import { describe, test } from "vitest";
import type { ServerEvent, TestResultPatch } from "../src/contract";
import { Store } from "../src/store";
import { applyResultPatch } from "../src/test-results";
import { catalog, every, fixed, flushFrames, installManualRaf, QUICK, stateEvent, testResultsEvent, testsUpdatedEvent } from "./fixtures";

installManualRaf();

/** A store holding catalog(n) (as after the on-connect snapshot). */
function loadedStore(n: number) {
  const s = new Store("mock");
  s.apply(stateEvent(catalog(n)));
  flushFrames();
  return s;
}

/** Alternating events (passed ⇄ failed), so every sample really changes the tests it touches. */
function alternating(make: (outcome: "passed" | "failed") => ServerEvent) {
  const a = make("failed");
  const b = make("passed");
  let flip = false;
  return () => ((flip = !flip) ? a : b);
}

describe("store", () => {
  test("state snapshot", async ({ bench }) => {
    const e1k = catalog(1_000);
    const e20k = catalog(20_000);
    const s = new Store("mock");
    await bench.compare(
      bench("apply state, 1,000 tests", () => {
        s.apply(stateEvent(e1k));
        flushFrames();
      }),
      bench("apply state, 20,000 tests", () => {
        s.apply(stateEvent(e20k));
        flushFrames();
      }),
      fixed(15),
    );
  });

  test("result batches into 20,000 tests (one frame each)", async ({ bench }) => {
    const tests = catalog(20_000).tests;
    const frame = (n: number, kind: "testsUpdated" | "testResults") => {
      const s = loadedStore(20_000);
      const changed = every(tests, Math.floor(tests.length / n));
      const next = alternating((o) => (kind === "testsUpdated" ? testsUpdatedEvent(changed, o) : testResultsEvent(changed, o)));
      return () => {
        s.apply(next());
        flushFrames();
      };
    };
    await bench.compare(
      bench("testsUpdated, 200 tests", frame(200, "testsUpdated")),
      bench("testsUpdated, 2,000 tests", frame(2_000, "testsUpdated")),
      bench("testResults, 200 patches", frame(200, "testResults")),
      bench("testResults, 2,000 patches", frame(2_000, "testResults")),
      QUICK,
    );
  });

  test("burst: 10 events of 100 in one frame", async ({ bench }) => {
    // The host sends many small events per frame while a run streams; the store copies the tests map once per frame.
    const tests = catalog(20_000).tests;
    const s = loadedStore(20_000);
    const chunks = Array.from({ length: 10 }, (_, i) => every(tests.slice(i * 2_000, (i + 1) * 2_000), 20));
    const updated = chunks.map((c) => alternating((o) => testsUpdatedEvent(c, o)));
    const results = chunks.map((c) => alternating((o) => testResultsEvent(c, o)));
    await bench.compare(
      bench("10 × testsUpdated(100) + publish", () => {
        for (const next of updated) s.apply(next());
        flushFrames();
      }),
      bench("10 × testResults(100) + publish", () => {
        for (const next of results) s.apply(next());
        flushFrames();
      }),
      QUICK,
    );
  });

  test("applyResultPatch", async ({ bench }) => {
    const tests = every(catalog(20_000).tests, 10).map((t) => ({ ...t, history: Array.from({ length: 20 }, (_, i) => ({ outcome: "passed" as const, durationMs: i, at: `2026-10-0${1 + (i % 9)}T10:00:00Z`, runId: `r${i}` })) }));
    const patches: TestResultPatch[] = tests.map((t) => ({ id: t.id, outcome: "failed", durationMs: 12, lastRunAt: "2026-10-02T10:00:00Z", errorMessage: "Assert.AreEqual failed.", recorded: true }) as TestResultPatch);
    await bench.compare(
      bench("2,000 patches (outcome + error, recorded in a full history)", () => {
        for (let i = 0; i < tests.length; i++) applyResultPatch(tests[i], patches[i], "run-9");
      }),
      bench("2,000 outcome-only patches", () => {
        for (let i = 0; i < tests.length; i++) applyResultPatch(tests[i], { id: tests[i].id, outcome: "running" }, "run-9");
      }),
      QUICK,
    );
  });
});
