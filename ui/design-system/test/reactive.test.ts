// Regression tests for the reactive core (scheduler, batch, ownership). Run: node scripts/wc/reactive-test.mjs (from ui/).
import { batch, computed, define, effect, h, keyed, LabcoatElement, list, reactiveStats, root, show, signal, untracked } from "../src/core";

type Test = { name: string; fn: () => void | Promise<void> };
const tests: Test[] = [];
const test = (name: string, fn: Test["fn"]) => tests.push({ name, fn });
function eq<T>(actual: T, expected: T, what = "value") {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) throw new Error(`${what}: expected ${e}, got ${a}`);
}

test("lead repro: batch() inside a running effect does not lose an update", () => {
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
  eq(log[log.length - 1], "B1", "last logged");
});

test("list() inside a keyed() region follows a key changed by a later effect in the same flush", () => {
  root((dispose) => {
    const a = signal("A");
    const p = signal(0);
    const host = h("div", null, keyed(() => a() + p(), (k) => h("section", { id: k }, list(() => [1, 2], (x) => x, (x) => h("i", null, () => String(x()))))));
    effect(() => {
      a();
      untracked(() => p.set(p.peek() + 1));
    });
    eq(host.querySelector("section")!.id, "A1", "after setup");
    a.set("B");
    eq(host.querySelector("section")!.id, "B2", "after a.set");
    eq(host.querySelectorAll("i").length, 2, "rows");
    dispose();
  });
});

test("show() region with a list re-evaluates when its condition flips during the same flush", () => {
  root((dispose) => {
    const go = signal(false);
    const flag = signal(false);
    const host = h("div", null, show(() => go() && flag(), () => h("b", null, list(() => ["x"], (x) => x, () => h("i"))), () => h("u")));
    effect(() => {
      if (go()) untracked(() => flag.set(true));
    });
    go.set(true);
    eq(!!host.querySelector("b"), true, "shown");
    dispose();
  });
});

test("nested batch: effects run once, after the outermost batch", () => {
  root((dispose) => {
    const a = signal(0);
    const b = signal(0);
    const runs: string[] = [];
    effect(() => runs.push(`${a()}-${b()}`));
    batch(() => {
      a.set(1);
      batch(() => b.set(2));
      eq(runs, ["0-0"], "inside outer batch");
    });
    eq(runs, ["0-0", "1-2"], "after batch");
    dispose();
  });
});

test("writes inside an effect apply after it ends (no re-entrant flush), in order", () => {
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
    eq(order, ["E start 2", "E end 2", "F 20"], "order");
    dispose();
  });
});

test("an effect that invalidates itself re-runs until stable", () => {
  root((dispose) => {
    const n = signal(0);
    let runs = 0;
    effect(() => {
      runs++;
      const v = n();
      if (v < 3) n.set(v + 1);
    });
    eq(n.peek(), 3, "n");
    eq(runs, 4, "runs");
    n.set(0);
    eq(n.peek(), 3, "n after reset");
    dispose();
  });
});

test("an effect that always invalidates itself is stopped by the guard (no hang)", () => {
  const errors: unknown[] = [];
  const orig = console.error;
  console.error = (...a: unknown[]) => errors.push(a);
  try {
    root((dispose) => {
      const n = signal(0);
      effect(() => n.set(n() + 1));
      if (n.peek() > 1000) throw new Error("did not stop");
      eq(errors.length > 0, true, "reported");
      dispose();
    });
  } finally {
    console.error = orig;
  }
});

test("writes during an effect's first run reach effects created before it", () => {
  root((dispose) => {
    const s = signal("a");
    const seen: string[] = [];
    effect(() => seen.push(s()));
    effect(() => s.set("b"));
    eq(seen, ["a", "b"], "seen");
    dispose();
  });
});

test("diamond: a computed depending on two paths of one signal runs once per change", () => {
  root((dispose) => {
    const s = signal(1);
    const l = computed(() => s() + 1);
    const r = computed(() => s() * 2);
    let runs = 0;
    const sum = computed(() => (runs++, l() + r()));
    const out: number[] = [];
    effect(() => out.push(sum()));
    s.set(2);
    eq(out, [4, 7], "values");
    eq(runs, 2, "computed runs");
    dispose();
  });
});

test("no leaks: effects and owners return to the baseline after dispose", () => {
  const before = reactiveStats();
  root((dispose) => {
    const items = signal([1, 2, 3]);
    const host = h("div", null, list(items, (x) => x, (x) => h("i", null, () => x())));
    items.set([3, 4]);
    eq(host.querySelectorAll("i").length, 2, "rows");
    dispose();
  });
  eq(reactiveStats(), before, "stats");
});

// ---- h(): props -------------------------------------------------------------------------------------

interface ProbeProps {
  path: string | undefined;
  label: string;
  line: number | null;
}
interface LcProbe extends ProbeProps {}
class LcProbe extends LabcoatElement<ProbeProps> {
  static defaults: ProbeProps = { path: undefined, label: "default", line: 1 };
  seen = () => ({ path: this.props.path(), label: this.props.label(), line: this.props.line() });
  protected render() {
    return null;
  }
}
define("lc-probe", LcProbe);
declare global {
  interface HTMLElementTagNameMap {
    "lc-probe": LcProbe;
  }
}

test("h(): an element prop bound reactively goes back to undefined / null (prop signal updates)", () => {
  root((dispose) => {
    const p = signal<string | undefined>("a.cs");
    const line = signal<number | null>(5);
    const el = h("lc-probe", { path: () => p(), line: () => line() });
    eq(el.seen().path, "a.cs", "path set");
    p.set(undefined);
    eq(el.seen().path, undefined, "path → undefined");
    p.set("b.cs");
    eq(el.seen().path, "b.cs", "path again");
    line.set(null);
    eq(el.seen().line, null, "line → null");
    dispose();
  });
});

test("h(): undefined on an element prop restores its default (static and reactive)", () => {
  root((dispose) => {
    const el = h("lc-probe", { label: undefined });
    eq(el.seen().label, "default", "static undefined");
    const l = signal<string | undefined>("x");
    const el2 = h("lc-probe", { label: () => l() });
    eq(el2.seen().label, "x", "set");
    l.set(undefined);
    eq(el2.seen().label, "default", "reactive undefined");
    dispose();
  });
});

test("h(): plain attributes / properties / class / style go away on null", () => {
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
    eq([el.title, el.getAttribute("data-x"), el.getAttribute("aria-label"), el.disabled, el.className, el.style.color], ["t", "t", "t", true, "t", "red"], "set");
    v.set(null);
    on.set(false);
    eq(
      [el.hasAttribute("title"), el.hasAttribute("data-x"), el.hasAttribute("aria-label"), el.hasAttribute("disabled"), el.className, el.style.color],
      [false, false, false, false, "", ""],
      "cleared",
    );
    dispose();
  });
});

export async function runAll() {
  const results: { name: string; ok: boolean; error?: string }[] = [];
  for (const t of tests) {
    try {
      await t.fn();
      results.push({ name: t.name, ok: true });
    } catch (e) {
      results.push({ name: t.name, ok: false, error: e instanceof Error ? e.stack ?? e.message : String(e) });
    }
  }
  return results;
}
