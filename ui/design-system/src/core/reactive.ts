/**
 * Thin typed layer over our scheduler (./scheduler.ts: alien-signals' graph algorithm + a non-re-entrant flush).
 *
 *   const count = signal(0);        count()  → read (tracked)     count.set(1) / count.update(n => n + 1)
 *   const double = computed(() => count() * 2);                  double()  → read (tracked, cached)
 *   const stop = effect(() => console.log(count()));             re-runs when count changes; stop() disposes
 *
 * Ownership. Upstream alien-signals links nested effects to the *tracking* context, which couples "who reads what"
 * with "who owns what". We keep the two apart (like Solid): the scheduler never nests effects in the graph; every
 * effect/root registers with the current {@link Owner};
 * disposing an owner disposes everything created under it (child effects, nested roots, `onCleanup` callbacks).
 * An effect gets a fresh child owner per run, so whatever it built last time is disposed before it re-runs.
 * Custom elements create a root owner on connect and dispose it on disconnect, so nothing outlives its DOM.
 */
import {
  createComputed,
  createEffect,
  createSignal,
  endBatch,
  readComputed,
  readSignal,
  setActiveSub,
  startBatch,
  writeSignal,
} from "./scheduler";

export type Accessor<T> = () => T;
export type Disposer = () => void;

export interface ReadSignal<T> {
  (): T;
  /** read without tracking */
  peek(): T;
}

export interface Signal<T> extends ReadSignal<T> {
  set(value: T): void;
  update(fn: (prev: T) => T): void;
}

/** Live counters (debug/leak checks): `window.__labcoat.stats()` in the app. */
const counters = { effects: 0, owners: 0 };
export const reactiveStats = () => ({ ...counters });

// ---- ownership -------------------------------------------------------------------------------------

export class Owner {
  private children?: Set<Owner>;
  private cleanups?: Disposer[];
  disposed = false;

  constructor(public parent: Owner | null) {
    counters.owners++;
    parent?.adopt(this);
  }

  private adopt(child: Owner) {
    if (this.disposed) {
      // Created under an owner that is already gone (e.g. a late async callback): dispose right away, never leak.
      queueMicrotask(() => child.dispose());
      return;
    }
    this.children ??= new Set();
    this.children.add(child);
  }

  onCleanup(fn: Disposer) {
    if (this.disposed) {
      fn();
      return;
    }
    this.cleanups ??= [];
    this.cleanups.push(fn);
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    counters.owners--;
    const prevSub = setActiveSub(undefined); // cleanups never track
    try {
      if (this.children) for (const c of [...this.children].reverse()) c.dispose();
      if (this.cleanups) for (let i = this.cleanups.length - 1; i >= 0; i--) this.cleanups[i]();
    } finally {
      setActiveSub(prevSub);
    }
    this.children = undefined;
    this.cleanups = undefined;
    this.parent?.children?.delete(this);
    this.parent = null;
  }
}

let currentOwner: Owner | null = null;

export const getOwner = () => currentOwner;

export function runWithOwner<T>(owner: Owner | null, fn: () => T): T {
  const prev = currentOwner;
  currentOwner = owner;
  try {
    return fn();
  } finally {
    currentOwner = prev;
  }
}

/** Register `fn` to run when the current owner (effect run, element, list row, root) is disposed. */
export function onCleanup(fn: Disposer) {
  if (currentOwner) currentOwner.onCleanup(fn);
  else if (import.meta.env?.DEV) console.warn("[reactive] onCleanup outside an owner never runs", fn);
}

/**
 * Run `fn` in a new owner (untracked). `parent` defaults to the current owner; pass `null` for a detached root
 * (custom elements do this: their lifetime follows the DOM, not their creator).
 */
export function root<T>(fn: (dispose: Disposer) => T, parent: Owner | null = currentOwner): T {
  const owner = new Owner(parent);
  return untracked(() => runWithOwner(owner, () => fn(() => owner.dispose())));
}

// ---- signals ---------------------------------------------------------------------------------------

export function signal<T>(initial: T): Signal<T> {
  const s = createSignal<T>(initial);
  const read = (() => readSignal(s)) as Signal<T>;
  read.peek = () => untracked(() => readSignal(s));
  read.set = (v: T) => writeSignal(s, v);
  read.update = (fn) => writeSignal(s, fn(untracked(() => readSignal(s))));
  return read;
}

/** Cached derivation; recomputes lazily and only notifies dependents when the result changes (`!==`). */
export function computed<T>(fn: (prev: T | undefined) => T): ReadSignal<T> {
  const c = createComputed<T>(fn);
  const read = (() => readComputed(c)) as ReadSignal<T>;
  read.peek = () => untracked(() => readComputed(c));
  return read;
}

/**
 * Run `fn` now and again whenever a signal it read changes. Owned by the current owner; anything created during a
 * run (child effects, onCleanup callbacks, DOM regions) is disposed before the next run and when the effect is.
 */
export function effect(fn: () => void): Disposer {
  const owner = new Owner(currentOwner);
  let run: Owner | undefined;
  counters.effects++;
  // The scheduler never links the effect into whatever is tracking right now: ownership is ours, not the graph's.
  // The first run is batched: writes made while it runs are applied (effects flushed) after it finished, never
  // re-entrantly from inside it. Re-runs happen inside the scheduler's flush loop, which is not re-entrant either.
  let stop: Disposer;
  startBatch();
  try {
    stop = createEffect(() => {
      run?.dispose();
      run = new Owner(owner);
      runWithOwner(run, fn);
    });
  } finally {
    endBatch();
  }
  owner.onCleanup(() => {
    stop();
    counters.effects--;
  });
  return () => owner.dispose();
}

/** Read signals in `fn` without subscribing the current effect/computed to them (ownership is kept). */
export function untracked<T>(fn: () => T): T {
  const prev = setActiveSub(undefined);
  try {
    return fn();
  } finally {
    setActiveSub(prev);
  }
}

/** Apply several writes; effects run once at the end. */
export function batch<T>(fn: () => T): T {
  startBatch();
  try {
    return fn();
  } finally {
    endBatch();
  }
}

/** `T` or a reactive accessor of `T` (what h() props accept). */
export type MaybeReactive<T> = T | Accessor<T>;

/** Read a maybe-reactive value (tracked when called inside an effect/computed). */
export const read = <T>(v: MaybeReactive<T>): T => (typeof v === "function" ? (v as Accessor<T>)() : v);
