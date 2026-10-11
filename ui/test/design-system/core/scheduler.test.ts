/**
 * Scheduler (design-system/src/core/scheduler.ts): our effect queue on top of `alien-signals/system`.
 * The behavioral regression tests live in reactive.test.ts; these pin the contract with the npm package and the
 * reason the scheduler exists (upstream's reference scheduler loses an update when a running effect flushes).
 */
import { describe, expect, it, vi } from "vitest";
import * as system from "alien-signals/system";
import * as upstream from "alien-signals";
import { batch, computed, effect, root, signal, untracked } from "../../../design-system/src/core";
import { createEffect, createSignal, endBatch, readSignal, startBatch, writeSignal } from "../../../design-system/src/core/scheduler";

describe("scheduler", () => {
  it("the system's flag values match the ones scheduler.ts mirrors (its typings use a const enum)", () => {
    const flags = (system as unknown as { ReactiveFlags: Record<string, number> }).ReactiveFlags;
    expect(flags).toEqual({ None: 0, Mutable: 1, Watching: 2, RecursedCheck: 4, Recursed: 8, Dirty: 16, Pending: 32 });
  });

  /**
   * The shape that loses an update in alien-signals' reference scheduler: E reads m, then flushes from inside its run
   * (an endBatch, like list() does), the nested flush runs W, W writes p, E already read m and is mid-run → not queued.
   * Same shape as scratchpad repro upstream-repro.mjs, through our wrappers.
   */
  it("a flush requested inside a running effect does not lose that effect's update", () => {
    root((dispose) => {
      const a = signal("A");
      const p = signal(0);
      const m = computed(() => a() + p());
      const log: string[] = [];
      effect(() => {
        log.push(m());
        batch(() => {});
      });
      effect(() => {
        const v = a();
        untracked(() => p.set(v === "A" ? 0 : 1));
      });
      a.set("B");
      expect(log[log.length - 1]).toBe("B1");
      expect(m()).toBe("B1");
      dispose();
    });
  });

  // Canary: if this starts failing after an alien-signals update, upstream's reference scheduler no longer loses the
  // update: reconsider whether we still need our own scheduler (ARCHITECTURE.md "Scheduling").
  it("upstream's reference scheduler (alien-signals main export) still loses that update", () => {
    const a = upstream.signal("A");
    const p = upstream.signal(0);
    const m = upstream.computed(() => a() + p());
    const log: string[] = [];
    const stopE = upstream.effect(() => {
      log.push(m());
      upstream.startBatch();
      upstream.endBatch();
    });
    const stopW = upstream.effect(() => {
      const v = a();
      const prev = upstream.setActiveSub(undefined);
      p(v === "A" ? 0 : 1);
      upstream.setActiveSub(prev);
    });
    a("B");
    expect(m()).toBe("B1");
    expect(log[log.length - 1]).toBe("B0");
    stopE();
    stopW();
  });

  it("an effect that disposes itself during its run drops the dependencies it read afterwards", () => {
    const s = createSignal(0);
    const t = createSignal(0);
    const fn = vi.fn();
    let stop: (() => void) | undefined;
    stop = createEffect(() => {
      fn();
      readSignal(s);
      if (readSignal(s) === 1) {
        stop!();
        readSignal(t); // read after disposal: must not keep the effect subscribed
      }
    });
    writeSignal(s, 1);
    expect(fn).toHaveBeenCalledTimes(2);
    writeSignal(t, 1);
    writeSignal(s, 2);
    expect(fn).toHaveBeenCalledTimes(2);
    expect(t.subs).toBeUndefined();
    expect(s.subs).toBeUndefined();
  });

  it("an effect that throws during a flush: the error propagates and the effects queued after it run on the next change", () => {
    const s = createSignal(0);
    const seen: number[] = [];
    const stopA = createEffect(() => {
      if (readSignal(s) === 1) throw new Error("boom");
    });
    const stopB = createEffect(() => void seen.push(readSignal(s)));
    expect(() => writeSignal(s, 1)).toThrow("boom");
    expect(seen).toEqual([0]);
    writeSignal(s, 2);
    expect(seen).toEqual([0, 2]);
    stopA();
    stopB();
  });

  it("startBatch/endBatch defer effects to the outermost endBatch", () => {
    const s = createSignal(0);
    const seen: number[] = [];
    const stop = createEffect(() => void seen.push(readSignal(s)));
    startBatch();
    writeSignal(s, 1);
    startBatch();
    writeSignal(s, 2);
    endBatch();
    expect(seen).toEqual([0]);
    endBatch();
    expect(seen).toEqual([0, 2]);
    stop();
  });
});
