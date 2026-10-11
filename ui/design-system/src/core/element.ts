/**
 * LabcoatElement: base class for lc-* custom elements (light DOM, no Shadow DOM, so the global stylesheet applies).
 *
 *   interface CounterProps { count: number; onPick: (n: number) => void }
 *   export interface Counter extends CounterProps {}               // typed props on the element: el.count = 3
 *   export class Counter extends LabcoatElement<CounterProps> {
 *     static defaults: CounterProps = { count: 0, onPick: () => {} };
 *     protected render() {
 *       return h("button", { class: "btn", onclick: () => this.props.onPick.peek()(this.props.count.peek()) },
 *         () => `Count: ${this.props.count()}`);
 *     }
 *   }
 *   define("lc-counter", Counter);
 *   declare global { interface HTMLElementTagNameMap { "lc-counter": Counter } }
 *
 * Lifecycle: `render()` runs once when the element connects, inside a fresh root owner (untracked); its result is
 * appended to the host. On disconnect the owner (all effects, lists, listeners registered with onCleanup) is disposed
 * and the children are removed; reconnecting renders again. A move within the document (disconnect + connect in the
 * same task, e.g. a keyed list reorder) keeps the rendered content.
 *
 * Props: every key of `static defaults` becomes a signal in `this.props` (read reactively: `this.props.count()`) and
 * a property accessor on the element, so parents set them with h(): `h("lc-counter", { count: () => n() })`.
 * Callback props must be named `on` + Uppercase (`onPick`) so h() assigns them instead of treating them as bindings.
 * h() always assigns element props, also when a binding becomes null/undefined; undefined restores the default.
 */
import { insert, type Child } from "./h";
import { root, signal, type Disposer, type Signal } from "./reactive";

export type PropSignals<P> = { readonly [K in keyof P]-?: Signal<P[K]> };

export abstract class LabcoatElement<P extends object = Record<never, never>> extends HTMLElement {
  /** Defaults for the reactive props (also the list of prop names). */
  static readonly defaults?: object;
  /** Class names for the host element (it takes the place of the root element of the React version). */
  static readonly hostClass?: string;

  protected readonly props: PropSignals<P>;
  #dispose?: Disposer;

  constructor() {
    super();
    const ctor = this.constructor as typeof LabcoatElement;
    const props: Record<string, Signal<unknown>> = {};
    const d = (ctor.defaults ?? {}) as Record<string, unknown>;
    for (const k of Object.keys(d)) props[k] = signal(d[k]);
    this.props = props as PropSignals<P>;
  }

  /** Build the element's content with h(); runs once per connection. */
  protected abstract render(): Child;

  connectedCallback() {
    if (this.#dispose) return; // moved, not removed
    const hostClass = (this.constructor as typeof LabcoatElement).hostClass;
    if (hostClass) this.classList.add(...hostClass.split(" "));
    this.#dispose = root((dispose) => {
      insert(this, this.render(), null);
      return dispose;
    }, null);
  }

  disconnectedCallback() {
    // Deferred so a move (disconnect + connect in the same task) does not tear down and rebuild the content.
    queueMicrotask(() => {
      if (this.isConnected || !this.#dispose) return;
      this.#dispose();
      this.#dispose = undefined;
      this.replaceChildren();
    });
  }
}

interface ElementClass extends CustomElementConstructor {
  defaults?: object;
  prototype: HTMLElement;
}

/** Register a custom element and install property accessors for its reactive props. */
export function define(tag: `lc-${string}`, cls: ElementClass) {
  if (customElements.get(tag)) return;
  for (const k of Object.keys(cls.defaults ?? {})) {
    if (import.meta.env?.DEV && k in HTMLElement.prototype) console.warn(`[${tag}] prop "${k}" shadows an HTMLElement property`);
    Object.defineProperty(cls.prototype, k, {
      configurable: true,
      get(this: { props: Record<string, Signal<unknown>> }) {
        return this.props[k]();
      },
      set(this: { props: Record<string, Signal<unknown>> }, v: unknown) {
        this.props[k].set(v);
      },
    });
  }
  customElements.define(tag, cls);
}
