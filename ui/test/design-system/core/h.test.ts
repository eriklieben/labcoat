/** h(): props / attributes / class / style, events vs callback props, reactive regions, fragment, list/show/keyed, cx. */
import { describe, expect, it, vi } from "vitest";
import {
  classString,
  cx,
  define,
  fragment,
  h,
  insert,
  keyed,
  LabcoatElement,
  list,
  reactiveStats,
  root,
  show,
  signal,
  type Child,
} from "../../../design-system/src/core";

/** Run `fn` in a root and dispose it afterwards. */
function scope<T>(fn: () => T): T {
  return root((dispose) => {
    try {
      return fn();
    } finally {
      queueMicrotask(dispose);
    }
  });
}

const texts = (host: Element, sel: string) => [...host.querySelectorAll(sel)].map((e) => e.textContent);

interface HProbeProps {
  value: number;
  onPick: (n: number) => void;
}
interface LcHProbe extends HProbeProps {}
class LcHProbe extends LabcoatElement<HProbeProps> {
  static defaults: HProbeProps = { value: 7, onPick: () => {} };
  protected render() {
    return h("span", null, () => this.props.value());
  }
}
define("lc-h-probe", LcHProbe);
declare global {
  interface HTMLElementTagNameMap {
    "lc-h-probe": LcHProbe;
  }
}

describe("h(): elements and props", () => {
  it("creates an element with static props, attributes and children", () => {
    const el = h("a", { href: "/x", title: "T", role: "link", "data-k": 3, "aria-current": true }, "go ", 1, null, false, undefined, true);
    expect(el.tagName).toBe("A");
    expect(el.getAttribute("href")).toBe("/x");
    expect(el.title).toBe("T");
    expect(el.getAttribute("role")).toBe("link");
    expect(el.getAttribute("data-k")).toBe("3");
    expect(el.getAttribute("aria-current")).toBe("true");
    expect(el.textContent).toBe("go 1");
  });

  it("accepts children without a props object (string, node, array)", () => {
    const el = h("p", "a", h("b", "b"), ["c", ["d"]]);
    expect(el.innerHTML).toBe("a<b>b</b>cd");
  });

  it("aria booleans become \"true\"/\"false\"; data/unknown false removes, true is empty", () => {
    const el = h("div", { "aria-expanded": false, "data-on": true, "data-off": false } as never);
    expect(el.getAttribute("aria-expanded")).toBe("false");
    expect(el.getAttribute("data-on")).toBe("");
    expect(el.hasAttribute("data-off")).toBe(false);
  });

  it("unknown names become attributes", () => {
    const el = h("div", { "x-custom": "v" } as never);
    expect(el.getAttribute("x-custom")).toBe("v");
  });

  it("DOM properties: value, tabIndex, disabled; null removes the attribute (value cleared)", () => {
    scope(() => {
      const v = signal<string | null>("abc");
      const t = signal<number | null>(0);
      const input = h("input", { value: () => v(), tabIndex: () => t(), readOnly: true });
      expect(input.value).toBe("abc");
      expect(input.tabIndex).toBe(0);
      expect(input.readOnly).toBe(true);
      v.set(null);
      t.set(null);
      expect(input.value).toBe("");
      expect(input.hasAttribute("tabindex")).toBe(false);
    });
  });

  it("htmlFor maps to the for attribute when removed", () => {
    scope(() => {
      const f = signal<string | undefined>("id1");
      const label = h("label", { htmlFor: () => f() });
      expect(label.htmlFor).toBe("id1");
      f.set(undefined);
      expect(label.hasAttribute("for")).toBe(false);
    });
  });

  it("reactive bindings write only when the value changed", () => {
    scope(() => {
      const s = signal("a");
      const el = h("div", { title: () => s().toUpperCase() });
      const spy = vi.spyOn(el, "setAttribute");
      let writes = 0;
      Object.defineProperty(el, "title", {
        configurable: true,
        get: () => el.getAttribute("title") ?? "",
        set: (v: string) => {
          writes++;
          el.setAttribute("title", v);
        },
      });
      s.set("A".toLowerCase()); // same computed value "A" → no write
      expect(writes).toBe(0);
      s.set("b");
      expect(writes).toBe(1);
      expect(el.title).toBe("B");
      spy.mockRestore();
    });
  });

  it("on + lowercase adds an event listener (never a binding); non-functions are ignored", () => {
    const fn = vi.fn();
    const el = h("button", { onclick: fn, onfocus: "nope" } as never);
    el.click();
    expect(fn).toHaveBeenCalledTimes(1);
    expect(fn.mock.calls[0][0]).toBeInstanceOf(Event);
    expect(el.getAttribute("onfocus")).toBeNull();
  });

  it("on + Uppercase is assigned as a callback prop (not called, not bound)", () => {
    const pick = vi.fn();
    const el = h("lc-h-probe", { onPick: pick });
    expect(el.onPick).toBe(pick);
    expect(pick).not.toHaveBeenCalled();
  });

  it("\"on\" alone and keys like \"on1\" are ordinary attributes", () => {
    const el = h("div", { on: "x", on1: "y" } as never);
    expect(el.getAttribute("on")).toBe("x");
    expect(el.getAttribute("on1")).toBe("y");
  });

  it("ref is called with the element after props and children", () => {
    let seen = "";
    const el = h("div", { class: "c", ref: (e) => (seen = `${e.className}:${e.childNodes.length}`) }, "x");
    expect(seen).toBe("c:1");
    expect(el.className).toBe("c");
  });

  it("element props: static values, reactive values; undefined restores the default", () => {
    scope(() => {
      const v = signal<number | undefined>(1);
      const el = h("lc-h-probe", { value: () => v() });
      expect(el.value).toBe(1);
      v.set(undefined);
      expect(el.value).toBe(7);
      const el2 = h("lc-h-probe", { value: 3 });
      expect(el2.value).toBe(3);
    });
  });
});

describe("h(): class", () => {
  it("string, array, object map, nested arrays and falsy parts", () => {
    expect(h("i", { class: "a b" }).className).toBe("a b");
    expect(h("i", { class: ["a", false, null, 0, ["b", undefined]] }).className).toBe("a b");
    expect(h("i", { class: { a: true, b: false, c: 1 } }).className).toBe("a c");
    expect(h("i", { class: "" }).className).toBe("");
  });

  it("reactive: a function, functions inside objects and arrays", () => {
    scope(() => {
      const on = signal(false);
      const name = signal("x");
      const a = h("i", { class: () => cx("base", on() && "on") });
      const b = h("i", { class: { base: true, on: () => on() } });
      const c = h("i", { class: ["base", { on: () => on() }] });
      const d = h("i", { class: () => ["k", name()] });
      expect([a.className, b.className, c.className, d.className]).toEqual(["base", "base", "base", "k x"]);
      on.set(true);
      name.set("y");
      expect([a.className, b.className, c.className, d.className]).toEqual(["base on", "base on", "base on", "k y"]);
    });
  });

  it("classString and cx", () => {
    expect(classString(null)).toBe("");
    expect(classString(["a", { b: () => true, c: () => false }])).toBe("a b");
    expect(cx("a", false, "b", null, undefined, 0, "")).toBe("a b");
    expect(cx()).toBe("");
  });
});

describe("h(): style", () => {
  it("a string sets cssText", () => {
    expect(h("i", { style: "color: red" }).style.color).toBe("red");
  });

  it("an object: numbers get px except unitless, custom properties as is", () => {
    const el = h("i", { style: { width: 24, opacity: 0.5, zIndex: 3, "--tree-w": 12, marginTop: "1em", "font-size": 10 } });
    expect(el.style.width).toBe("24px");
    expect(el.style.opacity).toBe("0.5");
    expect(el.style.zIndex).toBe("3");
    expect(el.style.getPropertyValue("--tree-w")).toBe("12");
    expect(el.style.marginTop).toBe("1em");
    expect(el.style.getPropertyValue("font-size")).toBe("10px");
  });

  it("reactive values per key; null/\"\" removes (also custom properties)", () => {
    scope(() => {
      const w = signal<number | null>(10);
      const el = h("i", { style: { width: () => w(), "--x": () => (w() ? `${w()}px` : null) } });
      expect(el.style.width).toBe("10px");
      expect(el.style.getPropertyValue("--x")).toBe("10px");
      w.set(null);
      expect(el.style.width).toBe("");
      expect(el.style.getPropertyValue("--x")).toBe("");
    });
  });

  it("a whole reactive style: string or object (each key re-applied, accessors read)", () => {
    scope(() => {
      const s = signal<string | { color: string; height: () => number } | null>("color: blue");
      const el = h("i", { style: () => s() as never });
      expect(el.style.color).toBe("blue");
      s.set({ color: "green", height: () => 5 });
      expect(el.style.color).toBe("green");
      expect(el.style.height).toBe("5px");
      s.set(null); // nothing applied, no throw
      expect(el.style.color).toBe("green");
    });
  });
});

describe("children: regions", () => {
  it("a function child is a region; primitive results update the text node in place", () => {
    scope(() => {
      const n = signal(1);
      const el = h("p", null, "n=", () => n());
      const textNode = [...el.childNodes].find((c) => c.nodeType === 3 && c.textContent === "1")!;
      n.set(2);
      expect(el.textContent).toBe("n=2");
      expect(textNode.textContent).toBe("2"); // same node
      n.set(2);
      expect(el.textContent).toBe("n=2");
    });
  });

  it("switches between nodes, text, arrays and nothing", () => {
    scope(() => {
      const mode = signal<"text" | "node" | "array" | "none" | "num">("text");
      const el = h("div", null, "[", (): Child => {
        switch (mode()) {
          case "text":
            return "t";
          case "num":
            return 5;
          case "node":
            return h("b", null, "B");
          case "array":
            return [h("i", null, "1"), "2"];
          default:
            return null;
        }
      }, "]");
      expect(el.textContent).toBe("[t]");
      mode.set("node");
      expect(el.innerHTML).toContain("<b>B</b>");
      mode.set("array");
      expect(el.textContent).toBe("[12]");
      mode.set("none");
      expect(el.textContent).toBe("[]");
      mode.set("num");
      expect(el.textContent).toBe("[5]");
      mode.set("text");
      expect(el.textContent).toBe("[t]");
    });
  });

  it("nested regions are disposed with the outer region's re-run", () => {
    root((dispose) => {
      const outer = signal(0);
      const inner = signal("a");
      const runs = vi.fn();
      const el = h("div", null, () => {
        outer();
        return h("span", null, () => (runs(), inner()));
      });
      outer.set(1);
      runs.mockClear();
      inner.set("b");
      expect(runs).toHaveBeenCalledTimes(1); // only the live inner region
      expect(el.textContent).toBe("b");
      dispose();
    });
  });

  it("fragment() and insert() handle every child kind", () => {
    const f = fragment("a", 1, null, [h("i"), "b"]);
    const host = document.createElement("div");
    host.append(f);
    expect(host.innerHTML).toBe("a1<i></i>b");
    const before = host.firstChild;
    insert(host, "z", before);
    expect(host.textContent).toBe("za1b");
    insert(host, false, null);
    expect(host.textContent).toBe("za1b");
  });
});

describe("show()", () => {
  it("renders while truthy (value accessor), the fallback otherwise; rebuilt only on flips", () => {
    scope(() => {
      const v = signal<string | null>("x");
      const renders = vi.fn();
      const el = h("div", null, show(v, (val) => (renders(), h("b", null, () => val())), () => h("u", null, "none")));
      expect(el.innerHTML).toContain("<b>");
      expect(el.textContent).toBe("x");
      v.set("y");
      expect(renders).toHaveBeenCalledTimes(1);
      expect(el.textContent).toBe("y");
      v.set(null);
      expect(el.textContent).toBe("none");
      v.set("z");
      expect(renders).toHaveBeenCalledTimes(2);
      expect(el.textContent).toBe("z");
    });
  });

  it("without a fallback renders nothing when falsy", () => {
    scope(() => {
      const on = signal(0);
      const el = h("div", null, show(on, () => "on"));
      expect(el.textContent).toBe("");
      on.set(1);
      expect(el.textContent).toBe("on");
    });
  });

  it("render runs untracked: a signal read there does not rebuild the branch", () => {
    scope(() => {
      const on = signal(true);
      const other = signal(1);
      const renders = vi.fn();
      h("div", null, show(on, () => (renders(), other())));
      other.set(2);
      expect(renders).toHaveBeenCalledTimes(1);
    });
  });
});

describe("keyed()", () => {
  it("rebuilds only when the key changes", () => {
    scope(() => {
      const a = signal(1);
      const b = signal(1);
      const renders = vi.fn((k: string) => h("i", null, k));
      const el = h("div", null, keyed(() => `${a()}-${b() > 0}`, renders));
      b.set(5);
      expect(renders).toHaveBeenCalledTimes(1);
      a.set(2);
      expect(renders).toHaveBeenCalledTimes(2);
      expect(el.textContent).toBe("2-true");
    });
  });
});

describe("list()", () => {
  type Item = { id: string; label: string };
  const item = (id: string, label = id) => ({ id, label });

  function setup(initial: Item[]) {
    const items = signal(initial);
    const created: string[] = [];
    let host!: HTMLElement;
    const dispose = root((d) => {
      host = h(
        "ul",
        null,
        list(
          items,
          (i) => i.id,
          (i, index) => {
            created.push(i.peek().id);
            return h("li", { "data-id": i.peek().id, "data-index": () => index() }, () => i().label);
          },
        ),
      );
      return d;
    });
    return { items, host, created, dispose };
  }

  it("renders rows in order and keeps row DOM per key across updates", () => {
    const { items, host, created, dispose } = setup([item("a"), item("b"), item("c")]);
    const liB = host.querySelector('[data-id="b"]');
    items.set([item("a"), item("b", "B!"), item("c")]);
    expect(host.querySelector('[data-id="b"]')).toBe(liB);
    expect(liB!.textContent).toBe("B!");
    expect(created).toEqual(["a", "b", "c"]);
    dispose();
  });

  it("reorders with moves (no re-creation) and updates the index signal", () => {
    const { items, host, created, dispose } = setup([item("a"), item("b"), item("c"), item("d")]);
    const nodes = Object.fromEntries([...host.querySelectorAll("li")].map((li) => [li.dataset.id!, li]));
    items.set([item("d"), item("b"), item("a"), item("c")]);
    expect([...host.querySelectorAll("li")].map((li) => li.dataset.id)).toEqual(["d", "b", "a", "c"]);
    expect([...host.querySelectorAll("li")].map((li) => li.dataset.index)).toEqual(["0", "1", "2", "3"]);
    expect(host.querySelector('[data-id="d"]')).toBe(nodes.d);
    expect(created).toEqual(["a", "b", "c", "d"]);
    dispose();
  });

  it("inserts new keys in place and removes + disposes missing ones", () => {
    const before = reactiveStats();
    const { items, host, created, dispose } = setup([item("a"), item("c")]);
    items.set([item("a"), item("b"), item("c"), item("d")]);
    expect(texts(host, "li")).toEqual(["a", "b", "c", "d"]);
    items.set([item("d")]);
    expect(texts(host, "li")).toEqual(["d"]);
    items.set([]);
    expect(host.querySelectorAll("li")).toHaveLength(0);
    expect(created).toEqual(["a", "c", "b", "d"]);
    dispose();
    expect(reactiveStats()).toEqual(before);
  });

  it("removing a row disposes its bindings", () => {
    root((dispose) => {
      const items = signal(["a", "b"]);
      const label = signal("x");
      const reads = vi.fn();
      h("ul", null, list(items, (k) => k, (k) => h("li", null, () => (reads(k.peek()), label()))));
      items.set(["a"]);
      reads.mockClear();
      label.set("y");
      expect(reads.mock.calls.map((c) => c[0])).toEqual(["a"]);
      dispose();
    });
  });

  it("duplicate keys keep the first and warn in dev", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    root((dispose) => {
      const el = h("ul", null, list(() => ["a", "a", "b"], (k) => k, (k) => h("li", null, k.peek())));
      expect(texts(el, "li")).toEqual(["a", "b"]);
      expect(warn).toHaveBeenCalledWith("[list] duplicate key", "a");
      dispose();
    });
  });

  it("rows that render multiple nodes or nothing are moved and removed as ranges", () => {
    root((dispose) => {
      const items = signal(["a", "b", "c"]);
      const el = h(
        "div",
        null,
        list(items, (k) => k, (k) => (k.peek() === "b" ? null : [h("i", null, k.peek()), h("u", null, k.peek())])),
      );
      expect(el.textContent).toBe("aacc");
      items.set(["c", "b", "a"]);
      expect(el.textContent).toBe("ccaa");
      items.set(["b"]);
      expect(el.textContent).toBe("");
      expect(el.querySelectorAll("i,u")).toHaveLength(0);
      dispose();
    });
  });

  it("a list as a direct child of a show() branch follows its items", () => {
    root((dispose) => {
      const on = signal(true);
      const items = signal([1, 2]);
      const el = h("div", null, show(on, () => h("ol", null, list(items, (x) => x, (x) => h("li", null, String(x.peek()))))));
      items.set([2, 3]);
      expect(texts(el, "li")).toEqual(["2", "3"]);
      on.set(false);
      expect(el.querySelector("ol")).toBeNull();
      items.set([4]); // the disposed list no longer reacts
      dispose();
    });
  });
});
