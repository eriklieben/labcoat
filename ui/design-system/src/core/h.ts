/**
 * h(): a tiny typed DOM builder with fine-grained reactive bindings. No virtual DOM: elements are created once and
 * only the bindings that read a changed signal touch the DOM.
 *
 *   h("button", { class: () => cx("btn", busy() && "busy"), disabled: () => !ready(), onclick: run }, icon("play"), "Run")
 *
 * Props
 *   - `onclick`, `onkeydown`, ... (`on` + lowercase)  → addEventListener (never reactive)
 *   - `onSelect`, `onRun`, ...   (`on` + Uppercase)  → assigned as-is: callback props of lc-* elements
 *   - any other function value                        → reactive: an effect re-applies it when its signals change
 *   - `class`: string | object map | array, each part may be reactive
 *   - `style`: string or `{ prop: value }` (camelCase or `--custom`), each value may be reactive; numbers get px
 *   - `aria-*`, `data-*`, `role` and unknown names    → attributes (null/undefined/false removes; aria gets "true"/"false")
 *   - everything else                                  → DOM property (`value`, `disabled`, `tabIndex`, `title`, ...)
 *   - `ref`: called with the element once props and children are in place
 *
 * Children: strings, numbers, nodes, arrays, null/undefined/false, or a function returning any of those. A function
 * becomes a region between two comment markers; when its signals change, the region is rebuilt (a plain string/number
 * result just updates the text node). Use `keyed`/`show` to rebuild only when a derived key changes, `list` for
 * keyed lists that keep row DOM across updates.
 */
import {
  batch,
  computed,
  effect,
  getOwner,
  root,
  signal,
  untracked,
  type Accessor,
  type Disposer,
  type MaybeReactive,
  type ReadSignal,
  type Signal,
} from "./reactive";

export type Child = Node | string | number | boolean | null | undefined | readonly Child[] | (() => Child);

export type ClassValue = string | false | null | undefined | 0 | { [name: string]: MaybeReactive<unknown> } | readonly ClassValue[];
/** One style property's value: null, undefined and "" clear it. */
type StylePropValue = string | number | null | undefined;
export type StyleValue = string | { [prop: string]: MaybeReactive<StylePropValue> };
type AttrValue = MaybeReactive<string | number | boolean | null | undefined>;

type Fn = (...args: never[]) => unknown;
type EventKeys<E> = { [K in keyof E]: K extends `on${string}` ? K : never }[keyof E];
type PropKeys<E> = {
  [K in keyof E]: K extends `on${string}` | "style" | "className" | "classList" ? never : E[K] extends Fn ? never : K;
}[keyof E];

/** Props accepted by h() for element type E. */
export type Props<E> = { [K in PropKeys<E>]?: MaybeReactive<E[K] | null | undefined> } & { [K in EventKeys<E>]?: E[K] } & {
  class?: MaybeReactive<ClassValue>;
  style?: MaybeReactive<StyleValue>;
  ref?: (el: E) => void;
  role?: AttrValue;
  [attr: `aria-${string}`]: AttrValue;
  [attr: `data-${string}`]: AttrValue;
};

const isPlainObject = (v: unknown): v is Record<string, unknown> =>
  !!v && typeof v === "object" && !(v instanceof Node) && !Array.isArray(v);

export function h<K extends keyof HTMLElementTagNameMap>(tag: K, props?: Props<HTMLElementTagNameMap[K]> | null, ...children: Child[]): HTMLElementTagNameMap[K];
export function h<K extends keyof HTMLElementTagNameMap>(tag: K, ...children: Child[]): HTMLElementTagNameMap[K];
export function h(tag: string, ...rest: unknown[]): HTMLElement {
  const el = document.createElement(tag);
  let props: Record<string, unknown> | undefined;
  if (rest.length && (rest[0] == null || isPlainObject(rest[0]))) props = (rest.shift() as Record<string, unknown>) ?? undefined;
  if (props) for (const key in props) if (key !== "ref") applyProp(el, key, props[key]);
  for (const c of rest as Child[]) insert(el, c, null);
  (props?.ref as ((el: HTMLElement) => void) | undefined)?.(el);
  return el;
}

/** Children without a wrapper element. */
export function fragment(...children: Child[]): DocumentFragment {
  const f = document.createDocumentFragment();
  for (const c of children) insert(f, c, null);
  return f;
}

// ---- props -----------------------------------------------------------------------------------------

const EVENT_PROP = /^on[a-z]/;
const CALLBACK_PROP = /^on[A-Z]/;

function applyProp(el: HTMLElement, key: string, value: unknown) {
  if (EVENT_PROP.test(key)) {
    // onclick → "click"
    if (typeof value === "function") el.addEventListener(key.slice(2), value as EventListener);
    return;
  }
  if (CALLBACK_PROP.test(key)) {
    // onSelect → callback prop of a custom element
    (el as unknown as Record<string, unknown>)[key] = value;
    return;
  }
  if (key === "class") return bind(value, (v) => classString(v as ClassValue), (s) => (el.className = s), true);
  if (key === "style") return applyStyle(el, value);
  bind(value, (v) => v, (v) => setProp(el, key, v));
}

/**
 * Apply `value` with `apply`; when it is reactive (a function, or for `deep` an object/array containing one) apply it
 * from an effect and skip DOM writes when the computed value did not change.
 */
function bind<T>(value: unknown, compute: (v: unknown) => T, apply: (v: T) => void, deep = false) {
  if (typeof value !== "function" && !(deep && hasFn(value))) return apply(compute(value));
  let last: T | typeof UNSET = UNSET;
  effect(() => {
    const v = compute(typeof value === "function" ? (value as Accessor<unknown>)() : value);
    if (v !== last) {
      last = v;
      apply(v);
    }
  });
}
const UNSET = Symbol();

function hasFn(v: unknown): boolean {
  if (typeof v === "function") return true;
  if (Array.isArray(v)) return v.some(hasFn);
  if (isPlainObject(v)) for (const k in v) if (typeof v[k] === "function") return true;
  return false;
}

/** cx-like: "a b", ["a", cond && "b"], { a: true, b: () => on() } → "a b" (reads functions: call inside effects). */
export function classString(v: ClassValue): string {
  if (!v) return "";
  if (typeof v === "string") return v;
  if (Array.isArray(v)) return classList(v);
  return classMap(v as Record<string, unknown>);
}

function classList(v: readonly ClassValue[]): string {
  let s = "";
  for (const x of v) {
    const c = classString(x);
    if (c) s = s ? `${s} ${c}` : c;
  }
  return s;
}

function classMap(v: Record<string, unknown>): string {
  let s = "";
  for (const k in v) if (readValue(v[k])) s = s ? `${s} ${k}` : k;
  return s;
}

const UNITLESS = new Set(["opacity", "zIndex", "flex", "flexGrow", "flexShrink", "fontWeight", "lineHeight", "order", "zoom"]);

function applyStyle(el: HTMLElement, value: unknown) {
  if (typeof value === "function") {
    // Whole style reactive: a string, or an object re-applied key by key.
    return effect(() => {
      const v = (value as Accessor<StyleValue>)();
      if (typeof v === "string") el.style.cssText = v;
      else if (v) for (const k in v) setStyle(el, k, readValue(v[k]));
    });
  }
  if (typeof value === "string") el.style.cssText = value;
  else if (isPlainObject(value)) for (const k in value) bind(value[k], (v) => v, (v) => setStyle(el, k, v as StylePropValue));
}

const readValue = <T>(v: MaybeReactive<T>) => (typeof v === "function" ? (v as Accessor<T>)() : v);

/** The CSS text for style `k`: "" clears it, numbers get px unless the property is unitless or custom. */
function styleText(k: string, v: StylePropValue): string {
  if (v == null || v === "") return "";
  if (typeof v === "number" && !UNITLESS.has(k) && !k.startsWith("--")) return `${v}px`;
  return String(v);
}

function setStyle(el: HTMLElement, k: string, v: StylePropValue) {
  const s = styleText(k, v);
  if (k.includes("-")) {
    if (s) el.style.setProperty(k, s);
    else el.style.removeProperty(k);
  } else (el.style as unknown as Record<string, string>)[k] = s;
}

const ATTR_NAME: Record<string, string> = { htmlFor: "for", className: "class", tabIndex: "tabindex", readOnly: "readonly" };

/** The `static defaults` of an lc-* element when `key` is one of its reactive props. */
function elementDefaults(el: HTMLElement, key: string): Record<string, unknown> | undefined {
  const d = (el.constructor as { defaults?: Record<string, unknown> }).defaults;
  return d && Object.hasOwn(d, key) ? d : undefined;
}

function setProp(el: HTMLElement, key: string, v: unknown) {
  // lc-* element props are signals: always assign, so a binding that goes back to undefined/null reaches the element.
  // undefined means "not given" → the prop's default (like React defaultProps); null is assigned as is.
  const defaults = elementDefaults(el, key);
  if (defaults) {
    (el as unknown as Record<string, unknown>)[key] = v === undefined ? defaults[key] : v;
    return;
  }
  const isAria = key.startsWith("aria-");
  if (isAria || key.startsWith("data-") || key === "role" || !(key in el)) {
    if (v == null || (v === false && !isAria)) el.removeAttribute(key);
    else el.setAttribute(key, v === true && !isAria ? "" : String(v as string | number | boolean));
    return;
  }
  if (v == null) {
    // Like React: undefined removes the attribute instead of writing "undefined".
    if (key === "value") (el as HTMLInputElement).value = "";
    el.removeAttribute(ATTR_NAME[key] ?? key.toLowerCase());
    return;
  }
  (el as unknown as Record<string, unknown>)[key] = v;
}

// ---- children --------------------------------------------------------------------------------------

/** Insert `child` into `parent` before `before` (null = append). Functions become reactive regions. */
export function insert(parent: Node, child: Child, before: Node | null) {
  if (child == null || typeof child === "boolean") return;
  if (typeof child === "string" || typeof child === "number") place(parent, before, document.createTextNode(String(child)));
  else if (typeof child === "function") region(parent, child, before);
  else if (Array.isArray(child)) for (const c of child as Child[]) insert(parent, c, before);
  else place(parent, before, child as Node);
}

/** `parent.insertBefore(node, before)` for each node, in order; `before` = null appends. */
function place(parent: Node, before: Node | null, ...nodes: Node[]) {
  if (before) (before as ChildNode).before(...nodes);
  else (parent as ParentNode).append(...nodes);
}

/** A reactive region: `fn`'s result lives between two comment markers and is replaced when `fn` re-runs. */
function region(parent: Node, fn: () => Child, before: Node | null) {
  const start = document.createComment("");
  const end = document.createComment("");
  place(parent, before, start, end);
  let text: Text | undefined; // fast path: a primitive result updates this text node in place
  effect(() => {
    const v = fn();
    if ((typeof v === "string" || typeof v === "number") && text?.previousSibling === start && text.nextSibling === end) {
      const s = String(v);
      if (text.data !== s) text.data = s;
      return;
    }
    clearBetween(start, end);
    text = undefined;
    if (typeof v === "string" || typeof v === "number") {
      text = document.createTextNode(String(v));
      end.before(text);
    } else insert(end.parentNode!, v, end);
  });
}

function clearBetween(start: Node, end: Node) {
  if (!end.parentNode) return;
  for (let n = start.nextSibling; n && n !== end; ) {
    const next = n.nextSibling;
    n.remove();
    n = next;
  }
}

// ---- control flow ------------------------------------------------------------------------------------

/**
 * Rebuild `render(key)` only when `key()` changes (`!==`). Reads inside `render` are not tracked by the region, so
 * bind reactive bits inside it with functions as usual.
 */
export function keyed<K>(key: () => K, render: (key: K) => Child): Child {
  const k = computed(key);
  return () => {
    const v = k();
    return untracked(() => render(v));
  };
}

/** `render` while `when()` is truthy (receives an accessor of the value), else `fallback`. Rebuilt only on flips. */
export function show<T>(when: () => T, render: (value: Accessor<NonNullable<T>>) => Child, fallback?: () => Child): Child {
  const on = computed(() => !!when());
  return () => {
    if (on()) return untracked(() => render(() => when() as NonNullable<T>));
    return fallback ? untracked(fallback) : null;
  };
}

interface Entry<T> {
  first: ChildNode;
  last: ChildNode;
  item: Signal<T>;
  index: Signal<number>;
  dispose: Disposer;
}

/**
 * Keyed list. Rows are created once per key (`render` runs untracked in its own owner) and reused while the key stays;
 * a new item object for an existing key is pushed into the row's `item` signal, so only bindings that read the
 * changed fields update. Rows that disappear are removed and disposed; moved rows are re-inserted with as few DOM
 * moves as a backwards walk allows. `render` should return one element (or a fragment whose top level is stable).
 */
export function list<T, K>(
  items: () => readonly T[],
  key: (item: T, index: number) => K,
  render: (item: ReadSignal<T>, index: ReadSignal<number>) => Child,
): DocumentFragment {
  const frag = document.createDocumentFragment();
  const start = document.createComment("list");
  const end = document.createComment("/list");
  frag.append(start, end);
  const owner = getOwner();
  let entries = new Map<K, Entry<T>>();

  const create = (it: T, i: number): Entry<T> =>
    root((dispose) => {
      const item = signal(it);
      const index = signal(i);
      const f = fragment(render(item, index));
      const first = f.firstChild ?? f.appendChild(document.createComment(""));
      const last = f.lastChild!;
      end.before(f);
      return { first, last, item, index, dispose };
    }, owner);

  effect(() => {
    const arr = items();
    untracked(() => {
      const next = new Map<K, Entry<T>>();
      const order: Entry<T>[] = [];
      batch(() => {
        for (let i = 0; i < arr.length; i++) {
          const it = arr[i];
          const k = key(it, i);
          if (next.has(k)) {
            if (import.meta.env?.DEV) console.warn("[list] duplicate key", k);
            continue;
          }
          let e = entries.get(k);
          if (e) {
            entries.delete(k);
            e.item.set(it);
            e.index.set(i);
          } else e = create(it, i);
          next.set(k, e);
          order.push(e);
        }
      });
      for (const e of entries.values()) {
        removeRange(e.first, e.last);
        e.dispose();
      }
      entries = next;
      // Place rows: walk backwards, moving a row only when it is not already right before the next one.
      let ref: ChildNode = end;
      for (let i = order.length - 1; i >= 0; i--) {
        const e = order[i];
        if (e.last.nextSibling !== ref) moveRange(e.first, e.last, ref);
        ref = e.first;
      }
    });
  });
  return frag;
}

function removeRange(first: ChildNode, last: ChildNode) {
  if (!first.parentNode) return;
  for (let n: ChildNode | null = first; n; ) {
    const next: ChildNode | null = n === last ? null : n.nextSibling;
    n.remove();
    n = next;
  }
}

function moveRange(first: ChildNode, last: ChildNode, before: ChildNode) {
  const nodes: ChildNode[] = [];
  for (let n: ChildNode | null = first; n; n = n === last ? null : n.nextSibling) nodes.push(n);
  before.before(...nodes);
}

/** Join truthy class names (for static strings; h()'s `class` prop also accepts arrays/objects directly). */
export function cx(...c: (string | false | null | undefined | 0)[]) {
  let s = "";
  for (const x of c) if (x) s = s ? `${s} ${x}` : x;
  return s;
}
