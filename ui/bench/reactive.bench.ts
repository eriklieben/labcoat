/**
 * Reactive core (design-system/src/core/reactive.ts + scheduler.ts): propagation through wide and deep graphs, batch,
 * effect flushing and owner disposal. Sizes follow the UI: a 20k-row tree is ~20k bindings, a frame touches a few
 * hundred to a few thousand of them.
 */
import { describe, test } from "vitest";
import { batch, computed, effect, root, signal, type Disposer, type ReadSignal } from "@eriklieben/labcoat-design-system/core";
import { QUICK } from "./fixtures";

describe("reactive core", () => {
  test("signal read/write", async ({ bench }) => {
    const s = signal(0);
    const c = computed(() => s() * 2);
    let i = 0;
    await bench.compare(
      bench("signal write (no subscribers)", () => s.set(++i)),
      bench("signal write + computed read", () => {
        s.set(++i);
        return c();
      }),
      bench("computed read (cached)", () => c()),
      QUICK,
    );
  });

  test("wide: 1 signal → N effects", async ({ bench }) => {
    const wide = (n: number) => {
      const s = signal(0);
      let sink = 0;
      root(() => {
        for (let i = 0; i < n; i++) effect(() => void (sink += s()));
      });
      let v = 0;
      return () => s.set(++v);
    };
    await bench.compare(bench("1 → 1,000 effects", wide(1_000)), bench("1 → 20,000 effects", wide(20_000)), QUICK);
  });

  test("wide: 1 signal → N computeds → 1 effect (fan-in)", async ({ bench }) => {
    const fan = (n: number) => {
      const s = signal(0);
      const cs: ReadSignal<number>[] = Array.from({ length: n }, (_, i) => computed(() => s() + i));
      const sum = computed(() => cs.reduce((a, c) => a + c(), 0));
      let sink = 0;
      root(() => effect(() => void (sink += sum())));
      let v = 0;
      return () => s.set(++v);
    };
    await bench.compare(bench("fan-in 1,000", fan(1_000)), bench("fan-in 10,000", fan(10_000)), QUICK);
  });

  test("deep: chain of N computeds → effect", async ({ bench }) => {
    const deep = (n: number) => {
      const s = signal(0);
      let c: ReadSignal<number> = s;
      for (let i = 0; i < n; i++) {
        const prev = c;
        c = computed(() => prev() + 1);
      }
      const last = c;
      let sink = 0;
      root(() => effect(() => void (sink += last())));
      let v = 0;
      return () => s.set(++v);
    };
    await bench.compare(bench("depth 100", deep(100)), bench("depth 1,000", deep(1_000)), QUICK);
  });

  test("diamond + equality cut-off", async ({ bench }) => {
    // 1 source → 1,000 computeds (parity: changes every other write) → 1,000 effects. Checks that unchanged computeds
    // stop propagation (an `!==` cut-off) at width.
    const s = signal(0);
    let runs = 0;
    root(() => {
      for (let i = 0; i < 1_000; i++) {
        const parity = computed(() => (s() + i) % 2);
        const same = computed(() => (s() >= 0 ? 1 : 0)); // never changes
        effect(() => void (runs += parity() + same()));
      }
    });
    let v = 0;
    await bench("1,000 diamonds, write", () => s.set(++v)).run(QUICK);
  });

  test("batch and scheduler flush", async ({ bench }) => {
    const N = 1_000;
    const sigs = Array.from({ length: N }, () => signal(0));
    let sink = 0;
    root(() => {
      for (const s of sigs) effect(() => void (sink += s()));
      effect(() => void (sink += sigs.reduce((a, s) => a + s(), 0))); // one effect reading all of them
    });
    let v = 0;
    await bench.compare(
      bench("1,000 writes, unbatched", () => {
        v++;
        for (const s of sigs) s.set(v);
      }),
      bench("1,000 writes in batch()", () => {
        v++;
        batch(() => {
          for (const s of sigs) s.set(v);
        });
      }),
      bench("nested batches (10 × 100)", () => {
        v++;
        batch(() => {
          for (let b = 0; b < 10; b++)
            batch(() => {
              for (let i = b * 100; i < (b + 1) * 100; i++) sigs[i].set(v);
            });
        });
      }),
      QUICK,
    );
  });

  test("write inside an effect (queued flush)", async ({ bench }) => {
    // A store-publish-like effect writing 1,000 derived signals that 1,000 effects read: the writes are queued and run
    // after the writing effect (scheduler rule), not re-entrantly.
    const src = signal(0);
    const out = Array.from({ length: 1_000 }, () => signal(0));
    let sink = 0;
    root(() => {
      effect(() => {
        const v = src();
        for (const o of out) o.set(v);
      });
      for (const o of out) effect(() => void (sink += o()));
    });
    let v = 0;
    await bench("1 → 1,000 writes → 1,000 effects", () => src.set(++v)).run(QUICK);
  });

  test("create and dispose", async ({ bench }) => {
    const s = signal(0);
    const make = (n: number) => () => {
      let dispose!: Disposer;
      root((d) => {
        dispose = d;
        for (let i = 0; i < n; i++) {
          const c = computed(() => s() + i);
          effect(() => void c());
        }
      });
      dispose();
    };
    await bench.compare(bench("1,000 computed+effect: create + dispose", make(1_000)), bench("10,000 computed+effect: create + dispose", make(10_000)), QUICK);
  });
});
