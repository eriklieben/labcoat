/** LabcoatElement / define(): render once per connection, dispose on disconnect, moves keep content, props, hostClass. */
import { describe, expect, it, vi } from "vitest";
import { define, effect, h, LabcoatElement, list, onCleanup, reactiveStats, root, signal } from "../../../design-system/src/core";
import { flushMicrotasks } from "../../helpers/dom";

const renders = vi.fn();
const cleanups = vi.fn();

interface CounterProps {
  count: number;
  label: string | undefined;
  onPick: (n: number) => void;
}
interface LcElCounter extends CounterProps {}
class LcElCounter extends LabcoatElement<CounterProps> {
  static defaults: CounterProps = { count: 0, label: undefined, onPick: () => {} };
  static hostClass = "counter-host extra";
  local = signal(0);
  protected render() {
    renders();
    onCleanup(cleanups);
    effect(() => {
      this.props.count(); // an effect owned by the element
    });
    return h(
      "button",
      { class: "btn", onclick: () => this.onPick(this.props.count.peek()) },
      () => `${this.props.label() ?? "Count"}: ${this.props.count()}`,
    );
  }
}
define("lc-el-counter", LcElCounter);

class LcElBare extends LabcoatElement {
  protected render() {
    return "bare";
  }
}
define("lc-el-bare", LcElBare);

declare global {
  interface HTMLElementTagNameMap {
    "lc-el-counter": LcElCounter;
    "lc-el-bare": LcElBare;
  }
}

describe("LabcoatElement", () => {
  it("renders once on connect, with the host class", () => {
    renders.mockClear();
    const el = document.createElement("lc-el-counter");
    expect(el.childNodes).toHaveLength(0);
    document.body.append(el);
    expect(renders).toHaveBeenCalledTimes(1);
    expect(el.classList.contains("counter-host")).toBe(true);
    expect(el.classList.contains("extra")).toBe(true);
    expect(el.textContent).toBe("Count: 0");
  });

  it("props are signals behind property accessors (defaults, set, reactive update)", () => {
    const el = document.createElement("lc-el-counter");
    expect(el.count).toBe(0);
    el.count = 3;
    el.label = "N";
    document.body.append(el);
    expect(el.textContent).toBe("N: 3");
    el.count = 4;
    expect(el.textContent).toBe("N: 4");
    expect(el.count).toBe(4);
  });

  it("callback props are called through the accessor", () => {
    const pick = vi.fn();
    const el = h("lc-el-counter", { count: 5, onPick: pick });
    document.body.append(el);
    el.querySelector("button")!.click();
    expect(pick).toHaveBeenCalledWith(5);
  });

  it("disposes on disconnect (after a microtask) and clears its children", async () => {
    const before = reactiveStats();
    cleanups.mockClear();
    const el = document.createElement("lc-el-counter");
    document.body.append(el);
    expect(reactiveStats().effects).toBeGreaterThan(before.effects);
    el.remove();
    expect(cleanups).not.toHaveBeenCalled();
    expect(el.textContent).toBe("Count: 0"); // still there until the microtask
    await flushMicrotasks();
    expect(cleanups).toHaveBeenCalledTimes(1);
    expect(el.childNodes).toHaveLength(0);
    expect(reactiveStats()).toEqual(before);
  });

  it("re-renders on reconnect after a removal; local signal fields survive", async () => {
    renders.mockClear();
    const el = document.createElement("lc-el-counter");
    document.body.append(el);
    el.local.set(9);
    el.remove();
    await flushMicrotasks();
    document.body.append(el);
    expect(renders).toHaveBeenCalledTimes(2);
    expect(el.local()).toBe(9);
    expect(el.textContent).toBe("Count: 0");
  });

  it("a move (disconnect + connect in the same task) keeps the content and does not re-render", async () => {
    renders.mockClear();
    cleanups.mockClear();
    const a = document.createElement("div");
    const b = document.createElement("div");
    document.body.append(a, b);
    const el = document.createElement("lc-el-counter");
    a.append(el);
    const btn = el.querySelector("button");
    b.append(el); // move
    await flushMicrotasks();
    expect(renders).toHaveBeenCalledTimes(1);
    expect(cleanups).not.toHaveBeenCalled();
    expect(el.querySelector("button")).toBe(btn);
  });

  it("a keyed list reorder moves elements without re-rendering them", async () => {
    renders.mockClear();
    const items = signal([1, 2, 3]);
    const dispose = root((d) => {
      document.body.append(h("div", null, list(items, (x) => x, (x) => h("lc-el-counter", { count: () => x() }))));
      return d;
    });
    items.set([3, 1, 2]);
    await flushMicrotasks();
    expect(renders).toHaveBeenCalledTimes(3);
    expect([...document.querySelectorAll("lc-el-counter")].map((e) => e.textContent)).toEqual(["Count: 3", "Count: 1", "Count: 2"]);
    dispose();
  });

  it("an element without defaults or hostClass renders its content", () => {
    const el = document.createElement("lc-el-bare");
    document.body.append(el);
    expect(el.textContent).toBe("bare");
    expect(el.className).toBe("");
  });

  it("connectedCallback twice without a disconnect does not render again", () => {
    renders.mockClear();
    const el = document.createElement("lc-el-counter");
    document.body.append(el);
    el.connectedCallback();
    expect(renders).toHaveBeenCalledTimes(1);
  });
});

describe("define()", () => {
  it("is idempotent for a tag that is already defined", () => {
    class Other extends LabcoatElement {
      protected render() {
        return null;
      }
    }
    expect(() => define("lc-el-counter", Other)).not.toThrow();
    expect(customElements.get("lc-el-counter")).toBe(LcElCounter);
  });

  it("warns in dev when a prop shadows an HTMLElement member", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    class Shadowing extends LabcoatElement<{ title: string }> {
      static defaults = { title: "x" };
      protected render() {
        return null;
      }
    }
    define("lc-el-shadowing", Shadowing);
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('prop "title" shadows'));
  });
});
