/**
 * Reactive core: signal / computed / effect / batch / untracked / root / onCleanup, ownership and the non-re-entrant
 * scheduler (core/scheduler.ts on alien-signals/system). The first block is the Vitest port of design-system/test/reactive.test.ts (the
 * 13 regression tests `npm run check:reactive` also runs in Chromium).
 */
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  batch,
  computed,
  define,
  effect,
  getOwner,
  h,
  keyed,
  LabcoatElement,
  list,
  onCleanup,
  Owner,
  reactiveStats,
  read,
  root,
  runWithOwner,
  show,
  signal,
  untracked,
} from "../../../design-system/src/core";

interface ProbeProps {
  path: string | undefined;
  label: string;
  line: number | null;
}
interface LcRxProbe extends ProbeProps {}
class LcRxProbe extends LabcoatElement<ProbeProps> {
  static defaults: ProbeProps = { path: undefined, label: "default", line: 1 };
  seen = () => ({ path: this.props.path(), label: this.props.label(), line: this.props.line() });
  protected render() {
    return null;
  }
}
define("lc-rx-probe", LcRxProbe);
declare global {
  interface HTMLElementTagNameMap {
    "lc-rx-probe": LcRxProbe;
  }
}

// ---- port of design-system/test/reactive.test.ts ------------------------------------------------------------------

describe("regressions (port of check:reactive)", () => {
  it("lead repro: batch() inside a running effect does not lose an update", () => {
    const log: string[] = [];
    root((dispose) => {
      const a = signal("A");
      const p = signal(0);
      const m = computed(() => a() + p());
      effect(() => {
        log.push(m());
        effect(() => batch(() => {}));
      });
      effect(() => {
        a();
        untracked(() => p.set(1));
      });
      a.set("B");
      dispose();
    });
    expect(log[log.length - 1]).toBe("B1");
  });

  it("list() inside a keyed() region follows a key changed by a later effect in the same flush", () => {
    root((dispose) => {
      const a = signal("A");
      const p = signal(0);
      const host = h(
        "div",
        null,
        keyed(
          () => a() + p(),
          (k) => h("section", { id: k }, list(() => [1, 2], (x) => x, (x) => h("i", null, () => String(x())))),
        ),
      );
      effect(() => {
        a();
        untracked(() => p.set(p.peek() + 1));
      });
      expect(host.querySelector("section")!.id).toBe("A1");
      a.set("B");
      expect(host.querySelector("section")!.id).toBe("B2");
      expect(host.querySelectorAll("i")).toHaveLength(2);
      dispose();
    });
  });

  it("show() region with a list re-evaluates when its condition flips during the same flush", () => {
    root((dispose) => {
      const go = signal(false);
      const flag = signal(false);
      const host = h("div", null, show(() => go() && flag(), () => h("b", null, list(() => ["x"], (x) => x, () => h("i"))), () => h("u")));
      effect(() => {
        if (go()) untracked(() => flag.set(true));
      });
      go.set(true);
      expect(host.querySelector("b")).not.toBeNull();
      dispose();
    });
  });

  it("nested batch: effects run once, after the outermost batch", () => {
    root((dispose) => {
      const a = signal(0);
      const b = signal(0);
      const runs: string[] = [];
      effect(() => runs.push(`${a()}-${b()}`));
      batch(() => {
        a.set(1);
        batch(() => b.set(2));
        expect(runs).toEqual(["0-0"]);
      });
      expect(runs).toEqual(["0-0", "1-2"]);
      dispose();
    });
  });

  it("writes inside an effect apply after it ends (no re-entrant flush), in order", () => {
    root((dispose) => {
      const x = signal(0);
      const y = signal(0);
      const order: string[] = [];
      effect(() => {
        const v = x();
        order.push(`E start ${v}`);
        batch(() => y.set(v * 10));
        order.push(`E end ${v}`);
      });
      effect(() => order.push(`F ${y()}`));
      order.length = 0;
      x.set(2);
      expect(order).toEqual(["E start 2", "E end 2", "F 20"]);
      dispose();
    });
  });

  it("an effect that invalidates itself re-runs until stable", () => {
    root((dispose) => {
      const n = signal(0);
      let runs = 0;
      effect(() => {
        runs++;
        const v = n();
        if (v < 3) n.set(v + 1);
      });
      expect(n.peek()).toBe(3);
      expect(runs).toBe(4);
      n.set(0);
      expect(n.peek()).toBe(3);
      dispose();
    });
  });

  it("an effect that always invalidates itself is stopped by the guard (no hang)", () => {
    const err = vi.spyOn(console, "error").mockImplementation(() => {});
    root((dispose) => {
      const n = signal(0);
      effect(() => n.set(n() + 1));
      expect(n.peek()).toBeLessThan(1000);
      expect(err).toHaveBeenCalled();
      expect(String(err.mock.calls[0][0])).toContain("re-ran more than");
      dispose();
    });
  });

  it("writes during an effect's first run reach effects created before it", () => {
    root((dispose) => {
      const s = signal("a");
      const seen: string[] = [];
      effect(() => seen.push(s()));
      effect(() => s.set("b"));
      expect(seen).toEqual(["a", "b"]);
      dispose();
    });
  });

  it("diamond: a computed depending on two paths of one signal runs once per change", () => {
    root((dispose) => {
      const s = signal(1);
      const l = computed(() => s() + 1);
      const r = computed(() => s() * 2);
      let runs = 0;
      const sum = computed(() => (runs++, l() + r()));
      const out: number[] = [];
      effect(() => out.push(sum()));
      s.set(2);
      expect(out).toEqual([4, 7]);
      expect(runs).toBe(2);
      dispose();
    });
  });

  it("no leaks: effects and owners return to the baseline after dispose", () => {
    const before = reactiveStats();
    root((dispose) => {
      const items = signal([1, 2, 3]);
      const host = h("div", null, list(items, (x) => x, (x) => h("i", null, () => x())));
      items.set([3, 4]);
      expect(host.querySelectorAll("i")).toHaveLength(2);
      dispose();
    });
    expect(reactiveStats()).toEqual(before);
  });


  it("h(): an element prop bound reactively goes back to undefined / null (prop signal updates)", () => {
    root((dispose) => {
      const p = signal<string | undefined>("a.cs");
      const line = signal<number | null>(5);
      const el = h("lc-rx-probe", { path: () => p(), line: () => line() });
      expect(el.seen().path).toBe("a.cs");
      p.set(undefined);
      expect(el.seen().path).toBeUndefined();
      p.set("b.cs");
      expect(el.seen().path).toBe("b.cs");
      line.set(null);
      expect(el.seen().line).toBeNull();
      dispose();
    });
  });

  it("h(): undefined on an element prop restores its default (static and reactive)", () => {
    root((dispose) => {
      const el = h("lc-rx-probe", { label: undefined });
      expect(el.seen().label).toBe("default");
      const l = signal<string | undefined>("x");
      const el2 = h("lc-rx-probe", { label: () => l() });
      expect(el2.seen().label).toBe("x");
      l.set(undefined);
      expect(el2.seen().label).toBe("default");
      dispose();
    });
  });

  it("h(): plain attributes / properties / class / style go away on null", () => {
    root((dispose) => {
      const v = signal<string | null>("t");
      const on = signal(true);
      const el = h("button", {
        title: () => v(),
        "data-x": () => v(),
        "aria-label": () => v(),
        disabled: () => (on() ? true : null),
        class: () => v(),
        style: { color: () => (v() ? "red" : null) },
      });
      expect([el.title, el.getAttribute("data-x"), el.getAttribute("aria-label"), el.disabled, el.className, el.style.color]).toEqual(["t", "t", "t", true, "t", "red"]);
      v.set(null);
      on.set(false);
      expect([
        el.hasAttribute("title"),
        el.hasAttribute("data-x"),
        el.hasAttribute("aria-label"),
        el.hasAttribute("disabled"),
        el.className,
        el.style.color,
      ]).toEqual([false, false, false, false, "", ""]);
      dispose();
    });
  });
});

// ---- signals and computeds ------------------------------------------------------------------------------------------

describe("signal", () => {
  it("reads, sets, updates and peeks", () => {
    const s = signal(1);
    expect(s()).toBe(1);
    s.set(2);
    expect(s()).toBe(2);
    s.update((n) => n * 10);
    expect(s.peek()).toBe(20);
  });

  it("notifies only on a changed value (!==)", () => {
    root((dispose) => {
      const obj = { a: 1 };
      const s = signal(obj);
      const fn = vi.fn(() => s());
      effect(fn);
      s.set(obj);
      expect(fn).toHaveBeenCalledTimes(1);
      s.set({ a: 1 });
      expect(fn).toHaveBeenCalledTimes(2);
      dispose();
    });
  });

  it("peek() and update() do not subscribe the running effect", () => {
    root((dispose) => {
      const s = signal(0);
      const t = signal(0);
      const fn = vi.fn(() => {
        s.peek();
        t();
      });
      effect(fn);
      s.set(5);
      expect(fn).toHaveBeenCalledTimes(1);
      t.set(1);
      expect(fn).toHaveBeenCalledTimes(2);
      dispose();
    });
  });

  it("read() reads plain values and accessors", () => {
    const s = signal("x");
    expect(read("y")).toBe("y");
    expect(read(s)).toBe("x");
    expect(read(() => 3)).toBe(3);
  });
});

describe("computed", () => {
  it("is lazy and cached", () => {
    const s = signal(2);
    const getter = vi.fn(() => s() * 2);
    const c = computed(getter);
    expect(getter).not.toHaveBeenCalled();
    expect(c()).toBe(4);
    expect(c()).toBe(4);
    expect(getter).toHaveBeenCalledTimes(1);
    s.set(3);
    expect(getter).toHaveBeenCalledTimes(1);
    expect(c.peek()).toBe(6);
    expect(getter).toHaveBeenCalledTimes(2);
  });

  it("receives the previous value", () => {
    const s = signal(1);
    const prevs: (number | undefined)[] = [];
    const c = computed<number>((prev) => (prevs.push(prev), s() + (prev ?? 0)));
    expect(c()).toBe(1);
    s.set(5);
    expect(c()).toBe(6);
    expect(prevs).toEqual([undefined, 1]);
  });

  it("does not notify dependents when the result is unchanged", () => {
    root((dispose) => {
      const s = signal(1);
      const parity = computed(() => s() % 2);
      const fn = vi.fn(() => parity());
      effect(fn);
      s.set(3);
      expect(fn).toHaveBeenCalledTimes(1);
      s.set(4);
      expect(fn).toHaveBeenCalledTimes(2);
      dispose();
    });
  });

  it("a computed chain stays glitch-free inside batch", () => {
    root((dispose) => {
      const first = signal("a");
      const last = signal("b");
      const full = computed(() => `${first()} ${last()}`);
      const seen: string[] = [];
      effect(() => seen.push(full()));
      batch(() => {
        first.set("x");
        last.set("y");
      });
      expect(seen).toEqual(["a b", "x y"]);
      dispose();
    });
  });

  it("a computed that creates effects in its getter: the child effects are dropped on recompute", () => {
    root((dispose) => {
      const s = signal(0);
      const inner = vi.fn();
      const c = computed(() => {
        const v = s();
        // an effect created while the computed is tracking (owned by the root; linked as a child effect)
        effect(() => inner(v));
        return v;
      });
      effect(() => c());
      s.set(1);
      expect(c()).toBe(1);
      expect(inner).toHaveBeenCalledWith(1);
      dispose();
    });
  });
});

// ---- effects, ownership -----------------------------------------------------------------------------------------------

describe("effect", () => {
  it("runs now and on change; the disposer stops it", () => {
    const s = signal(0);
    const seen: number[] = [];
    const stop = effect(() => seen.push(s()));
    s.set(1);
    stop();
    s.set(2);
    expect(seen).toEqual([0, 1]);
  });

  it("disposes what a run created (child effects, onCleanup) before the next run", () => {
    root((dispose) => {
      const s = signal(0);
      const child = signal(0);
      const childRuns: string[] = [];
      const cleanups: number[] = [];
      effect(() => {
        const v = s();
        onCleanup(() => cleanups.push(v));
        effect(() => childRuns.push(`${v}:${child()}`));
      });
      child.set(1);
      s.set(1);
      child.set(2);
      expect(cleanups).toEqual([0]);
      // the effect from run 0 is gone: only run 1's child reacts to child=2
      expect(childRuns).toEqual(["0:0", "0:1", "1:1", "1:2"]);
      dispose();
      expect(cleanups).toEqual([0, 1]);
    });
  });

  it("is owned by the current owner: disposing the root stops it", () => {
    const s = signal(0);
    const fn = vi.fn(() => s());
    root((dispose) => {
      effect(fn);
      dispose();
    });
    s.set(1);
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("an effect created inside another effect is not re-run by the parent's dependencies", () => {
    root((dispose) => {
      const a = signal(0);
      const b = signal(0);
      const inner = vi.fn(() => b());
      const outer = vi.fn(() => {
        a();
        untracked(() => effect(inner));
      });
      effect(outer);
      b.set(1);
      expect(outer).toHaveBeenCalledTimes(1);
      expect(inner).toHaveBeenCalledTimes(2);
      dispose();
    });
  });

  it("reactiveStats counts live effects and owners", () => {
    const before = reactiveStats();
    const stop = effect(() => {});
    expect(reactiveStats().effects).toBe(before.effects + 1);
    expect(reactiveStats().owners).toBeGreaterThan(before.owners);
    stop();
    expect(reactiveStats()).toEqual(before);
  });

  it("the loop guard only applies per flush: the next change runs the effect again", () => {
    const err = vi.spyOn(console, "error").mockImplementation(() => {});
    root((dispose) => {
      const n = signal(0);
      const other = signal(0);
      let runs = 0;
      effect(() => {
        runs++;
        other();
        n.set(n() + 1);
      });
      const afterFirst = runs;
      expect(afterFirst).toBeGreaterThan(100);
      expect(afterFirst).toBeLessThan(110);
      other.set(1);
      expect(runs).toBeGreaterThan(afterFirst);
      expect(err).toHaveBeenCalledTimes(2);
      dispose();
    });
  });

  it("an effect re-queued after its run is skipped when the dependency settled back (no real change)", () => {
    root((dispose) => {
      const s = signal(0);
      const c = computed(() => s() > 0);
      let runs = 0;
      effect(() => {
        runs++;
        c();
        // write and revert: the computed ends up unchanged
        untracked(() => {
          s.set(1);
          s.set(0);
        });
      });
      expect(runs).toBe(1);
      dispose();
    });
  });

  it("an exception in an effect propagates and does not break later flushes", () => {
    root((dispose) => {
      const s = signal(0);
      const seen: number[] = [];
      effect(() => {
        if (s() === 1) throw new Error("boom");
        seen.push(s());
      });
      expect(() => s.set(1)).toThrow("boom");
      s.set(2);
      expect(seen).toEqual([0, 2]);
      dispose();
    });
  });
});

describe("batch / untracked", () => {
  it("batch returns the function's value and flushes even when it throws", () => {
    root((dispose) => {
      const s = signal(0);
      const seen: number[] = [];
      effect(() => seen.push(s()));
      expect(batch(() => (s.set(1), "ok"))).toBe("ok");
      expect(() =>
        batch(() => {
          s.set(2);
          throw new Error("x");
        }),
      ).toThrow("x");
      expect(seen).toEqual([0, 1, 2]);
      dispose();
    });
  });

  it("untracked returns the value and keeps the owner", () => {
    root((dispose) => {
      const owner = getOwner();
      const s = signal(4);
      let inside: Owner | null = null;
      const fn = vi.fn(() => untracked(() => ((inside = getOwner()), s())));
      effect(fn);
      s.set(5);
      expect(fn).toHaveBeenCalledTimes(1);
      expect(inside).not.toBeNull();
      expect(inside).not.toBe(owner); // the effect run's owner
      dispose();
    });
  });
});

describe("root / Owner / onCleanup", () => {
  it("root runs untracked in a new owner and passes a disposer", () => {
    const outer = signal(0);
    const fn = vi.fn();
    effect(() => {
      outer();
      root(() => {
        fn();
        outer(); // untracked: does not subscribe the effect again (it already did)
      });
    });
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("disposes children (in reverse) and cleanups (in reverse), once", () => {
    const order: string[] = [];
    let disposeRoot!: () => void;
    root((dispose) => {
      disposeRoot = dispose;
      onCleanup(() => order.push("root-1"));
      root(() => onCleanup(() => order.push("child-a")));
      root(() => onCleanup(() => order.push("child-b")));
      onCleanup(() => order.push("root-2"));
    });
    disposeRoot();
    disposeRoot();
    expect(order).toEqual(["child-b", "child-a", "root-2", "root-1"]);
  });

  it("a child root disposed on its own leaves the parent intact", () => {
    const order: string[] = [];
    root((dispose) => {
      root((d) => {
        onCleanup(() => order.push("child"));
        d();
      });
      expect(order).toEqual(["child"]);
      onCleanup(() => order.push("parent"));
      dispose();
    });
    expect(order).toEqual(["child", "parent"]);
  });

  it("a detached root (parent null) is not disposed with the current owner", () => {
    const cleaned = vi.fn();
    let detached!: () => void;
    root((dispose) => {
      root((d) => {
        detached = d;
        onCleanup(cleaned);
      }, null);
      dispose();
    });
    expect(cleaned).not.toHaveBeenCalled();
    detached();
    expect(cleaned).toHaveBeenCalledTimes(1);
  });

  it("onCleanup on a disposed owner runs at once; an owner created under it is disposed in a microtask", async () => {
    const owner = new Owner(null);
    owner.dispose();
    const fn = vi.fn();
    owner.onCleanup(fn);
    expect(fn).toHaveBeenCalledTimes(1);
    const late = vi.fn();
    runWithOwner(owner, () => root(() => onCleanup(late)));
    expect(late).not.toHaveBeenCalled();
    await Promise.resolve();
    expect(late).toHaveBeenCalledTimes(1);
  });

  it("onCleanup without an owner warns in dev and never runs", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const fn = vi.fn();
    runWithOwner(null, () => onCleanup(fn));
    expect(warn).toHaveBeenCalled();
    expect(fn).not.toHaveBeenCalled();
  });

  it("cleanups never track: a signal read in a cleanup does not subscribe the effect", () => {
    root((dispose) => {
      const s = signal(0);
      const t = signal(0);
      const fn = vi.fn(() => {
        s();
        onCleanup(() => t());
      });
      effect(fn);
      s.set(1); // runs the cleanup (reads t) and the effect again
      t.set(1);
      expect(fn).toHaveBeenCalledTimes(2);
      dispose();
    });
  });

  it("runWithOwner restores the previous owner even when fn throws", () => {
    const o = new Owner(null);
    const before = getOwner();
    expect(() =>
      runWithOwner(o, () => {
        expect(getOwner()).toBe(o);
        throw new Error("x");
      }),
    ).toThrow("x");
    expect(getOwner()).toBe(before);
    o.dispose();
  });
});

afterEach(() => {
  vi.restoreAllMocks();
});

// ---- dependency graph (alien-signals/system) ---------------------------------------------------------------------------

describe("dependency graph", () => {
  it("dynamic dependencies: a branch no longer read stops notifying", () => {
    root((dispose) => {
      const useA = signal(true);
      const a = signal("a");
      const b = signal("b");
      const fn = vi.fn(() => (useA() ? a() : b()));
      effect(fn);
      useA.set(false);
      fn.mockClear();
      a.set("a2");
      expect(fn).not.toHaveBeenCalled();
      b.set("b2");
      expect(fn).toHaveBeenCalledTimes(1);
      dispose();
    });
  });

  it("a deep computed chain is checked lazily and recomputes only what changed", () => {
    root((dispose) => {
      const s = signal(1);
      const getters = [vi.fn(), vi.fn(), vi.fn(), vi.fn()];
      const c1 = computed(() => (getters[0](), s() % 2));
      const c2 = computed(() => (getters[1](), c1() + 1));
      const c3 = computed(() => (getters[2](), c2() * 10));
      const c4 = computed(() => (getters[3](), `v${c3()}`));
      const seen: string[] = [];
      effect(() => seen.push(c4()));
      s.set(3); // c1 unchanged → c2..c4 not recomputed
      expect(getters.map((g) => g.mock.calls.length)).toEqual([2, 1, 1, 1]);
      s.set(4);
      expect(seen).toEqual(["v20", "v10"]);
      dispose();
    });
  });

  it("several subscribers of one computed all update (shallow propagation)", () => {
    root((dispose) => {
      const s = signal(1);
      const c = computed(() => s() * 2);
      const a: number[] = [];
      const b: number[] = [];
      const d = computed(() => c() + 1);
      effect(() => a.push(c()));
      effect(() => b.push(d()));
      effect(() => c());
      s.set(2);
      expect(a).toEqual([2, 4]);
      expect(b).toEqual([3, 5]);
      dispose();
    });
  });

  it("an unwatched computed drops its dependencies and recomputes on the next read", () => {
    const s = signal(1);
    const getter = vi.fn(() => s() + 1);
    const c = computed(getter);
    const stop = effect(() => c());
    stop();
    s.set(5);
    expect(c()).toBe(6);
    expect(getter).toHaveBeenCalledTimes(2);
  });

  it("a computed read twice in one effect run links once", () => {
    root((dispose) => {
      const s = signal(1);
      const c = computed(() => s());
      const fn = vi.fn(() => c() + c() + s() + s());
      effect(fn);
      s.set(2);
      expect(fn).toHaveBeenCalledTimes(2);
      dispose();
    });
  });

  it("an effect reading a computed that a sibling effect invalidates sees the final value", () => {
    root((dispose) => {
      const src = signal(0);
      const mirror = signal(0);
      const sum = computed(() => src() + mirror());
      const seen: number[] = [];
      effect(() => {
        const v = src();
        untracked(() => mirror.set(v));
      });
      effect(() => seen.push(sum()));
      src.set(1);
      expect(seen[seen.length - 1]).toBe(2);
      dispose();
    });
  });

  it("a write inside a computed's dependency during an effect's run is picked up (inner write)", () => {
    root((dispose) => {
      const a = signal(0);
      const c = computed(() => a() * 2);
      const seen: number[] = [];
      effect(() => {
        seen.push(c());
        if (a() === 0) a.set(1);
      });
      expect(seen).toEqual([0, 2]);
      dispose();
    });
  });
});
