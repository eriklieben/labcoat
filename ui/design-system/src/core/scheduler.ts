/**
 * Labcoat's signal scheduler, built on the dependency-graph algorithm of alien-signals (`alien-signals/system`, used
 * unmodified from npm: link / unlink / propagate / checkDirty / shallowPropagate).
 *
 * The system only maintains the graph and its flags; *when* effects run is up to the code around it. alien-signals
 * ships a reference scheduler as its main export, but we need different semantics there:
 *
 * 1. `flush()` is not re-entrant. Upstream, an `endBatch()` (or a write to a watched signal) inside a running effect
 *    flushes the queue from inside that effect. An effect run there can change something the running effect already
 *    read; the running effect is then marked but not queued (propagate never queues an effect mid-run), so the update
 *    is lost. `list()` batches inside its effect, so this is reachable from ordinary component code. Here nested flush
 *    requests are no-ops: the outermost flush loop picks up everything queued meanwhile, after the running effect ended.
 * 2. An effect that was notified during its own run (it wrote a signal it had already read, directly or through a
 *    computed) is queued again when the run ends, so it re-runs until stable. A per-flush run limit stops effects that
 *    keep invalidating themselves (reported with console.error, then left alone until the next change).
 * 3. Effects are never linked into the tracking graph as children of whatever was tracking when they were created.
 *    Ownership (which effect disposes which) lives in ./reactive.ts, so the scheduler has no nested-effect or effect-
 *    scope bookkeeping, and effects have no cleanup return value (reactive.ts has onCleanup for that).
 *
 * The node operations below (reading/writing signals, computing, running an effect, disposing) speak the system's flag
 * protocol and are adapted from alien-signals' reference implementation (alien-signals 3.2.1, src/index.ts,
 * MIT License, Copyright (c) 2024-present Johnson Chu; see ./LICENSE-alien-signals.txt, which ships with this file).
 * The queueing and flushing (`enqueue`, `finishRun`, `flush`, the run limit) are ours.
 *
 * Only ./reactive.ts imports this module; app code uses the wrappers there.
 */
import { createReactiveSystem, type Link, type ReactiveNode } from "alien-signals/system";

// The system's ReactiveFlags (a const enum in its typings, which `isolatedModules` can't inline from a .d.ts).
// Same values; checked against the package's runtime export in test/design-system/core/scheduler.test.ts.
const enum Flag {
  None = 0,
  Mutable = 1,
  Watching = 2,
  RecursedCheck = 4,
  Recursed = 8,
  Dirty = 16,
  Pending = 32,
}

/** Effects re-run at most this often per flush; more means an effect keeps invalidating itself. */
const MAX_RUNS_PER_FLUSH = 100;

// ---- nodes -------------------------------------------------------------------------------------------------------

export class SignalNode<T = unknown> implements ReactiveNode {
  subs: Link | undefined = undefined;
  subsTail: Link | undefined = undefined;
  flags: number = Flag.Mutable;
  /** the value readers see; `pendingValue` is the last written one, applied on the next read */
  currentValue: T;
  pendingValue: T;
  constructor(value: T) {
    this.currentValue = this.pendingValue = value;
  }
}

export class ComputedNode<T = unknown> implements ReactiveNode {
  deps: Link | undefined = undefined;
  depsTail: Link | undefined = undefined;
  subs: Link | undefined = undefined;
  subsTail: Link | undefined = undefined;
  flags: number = Flag.None;
  value: T | undefined = undefined;
  constructor(readonly getter: (previous: T | undefined) => T) {}
}

class EffectNode implements ReactiveNode {
  deps: Link | undefined = undefined;
  depsTail: Link | undefined = undefined;
  // effects are never a dependency of anything (see 3. above), but keep the shape the system expects
  subs: Link | undefined = undefined;
  subsTail: Link | undefined = undefined;
  flags: number = Flag.Watching | Flag.RecursedCheck;
  /** run limit bookkeeping: the flush `runs` counts for */
  flushId = 0;
  runs = 0;
  constructor(readonly fn: () => void) {}
}

// ---- state ---------------------------------------------------------------------------------------------------------

/** the effect or computed currently tracking reads (undefined: untracked) */
let activeSub: ReactiveNode | undefined;
/** link version; bumped per (re)computation so a dependency read twice in one run links once */
let cycle = 0;
/** > 0 while an effect body runs: writes then are "inner writes" for propagate() */
let runDepth = 0;
let batchDepth = 0;
let flushing = false;
let flushId = 0;
const queue: (EffectNode | undefined)[] = [];
let queueHead = 0;
let queueLength = 0;

const { link, unlink, propagate, checkDirty, shallowPropagate } = createReactiveSystem({
  // Only signals and computeds are ever dependencies here (effects are not linked as children).
  update: (node) => (node instanceof ComputedNode ? updateComputed(node) : updateSignal(node as SignalNode)),
  notify: (node) => enqueue(node as EffectNode),
  unwatched(node) {
    // A computed nobody watches drops its own dependencies and recomputes on its next read.
    if (node instanceof ComputedNode && node.depsTail !== undefined) {
      node.flags = Flag.Mutable | Flag.Dirty;
      disposeDeps(node);
    }
  },
});

// ---- tracking and batching -----------------------------------------------------------------------------------------

/** Make `sub` the tracking context (undefined: untracked); returns the previous one to restore. */
export function setActiveSub(sub: ReactiveNode | undefined): ReactiveNode | undefined {
  const prev = activeSub;
  activeSub = sub;
  return prev;
}

export function startBatch(): void {
  ++batchDepth;
}

export function endBatch(): void {
  if (!--batchDepth) flush();
}

// ---- signals and computeds ------------------------------------------------------------------------------------------

export const createSignal = <T>(value: T): SignalNode<T> => new SignalNode(value);

export function readSignal<T>(s: SignalNode<T>): T {
  if (s.flags & Flag.Dirty && updateSignal(s) && s.subs !== undefined) shallowPropagate(s.subs);
  if (activeSub !== undefined) link(s, activeSub, cycle);
  return s.currentValue;
}

export function writeSignal<T>(s: SignalNode<T>, value: T): void {
  if (s.pendingValue === (s.pendingValue = value)) return;
  s.flags = Flag.Mutable | Flag.Dirty;
  if (s.subs !== undefined) {
    propagate(s.subs, runDepth > 0);
    if (!batchDepth) flush();
  }
}

function updateSignal(s: SignalNode): boolean {
  s.flags = Flag.Mutable;
  return s.currentValue !== (s.currentValue = s.pendingValue);
}

export const createComputed = <T>(getter: (previous: T | undefined) => T): ComputedNode<T> => new ComputedNode(getter);

export function readComputed<T>(c: ComputedNode<T>): T {
  const flags = c.flags;
  if (flags & Flag.Dirty || (flags & Flag.Pending && (checkDirty(c.deps!, c) || ((c.flags = flags & ~Flag.Pending), false)))) {
    // stale: recompute, and tell readers that only got "maybe changed" (Pending) that it did change
    if (updateComputed(c) && c.subs !== undefined) shallowPropagate(c.subs);
  } else if (flags === Flag.None) {
    // first read
    c.flags = Flag.Mutable | Flag.RecursedCheck;
    const prev = setActiveSub(c);
    try {
      c.value = c.getter(undefined);
    } finally {
      activeSub = prev;
      c.flags &= ~Flag.RecursedCheck;
    }
  }
  if (activeSub !== undefined) link(c, activeSub, cycle);
  return c.value as T;
}

/** Recompute; true when the value changed (`!==`). */
function updateComputed<T>(c: ComputedNode<T>): boolean {
  c.depsTail = undefined;
  c.flags = Flag.Mutable | Flag.RecursedCheck;
  const prev = setActiveSub(c);
  try {
    ++cycle;
    const old = c.value;
    return old !== (c.value = c.getter(old));
  } finally {
    activeSub = prev;
    c.flags &= ~Flag.RecursedCheck;
    purgeDeps(c);
  }
}

// ---- effects ------------------------------------------------------------------------------------------------------

/**
 * Run `fn` now (tracking its reads) and again whenever one of them changes. Returns the disposer. The effect is not
 * linked to the current tracking context: ./reactive.ts owns effect lifetimes.
 */
export function createEffect(fn: () => void): () => void {
  const e = new EffectNode(fn);
  const prev = setActiveSub(e);
  try {
    ++runDepth;
    e.fn();
  } finally {
    --runDepth;
    activeSub = prev;
    endRun(e);
  }
  finishRun(e);
  return () => disposeEffect(e);
}

/** Run a queued effect if one of its dependencies really changed (Dirty, or Pending and confirmed by checkDirty). */
function run(e: EffectNode): void {
  const flags = e.flags;
  if (flags & Flag.Dirty || (flags & Flag.Pending && checkDirty(e.deps!, e))) {
    if (e.flushId !== flushId) {
      e.flushId = flushId;
      e.runs = 0;
    }
    if (++e.runs > MAX_RUNS_PER_FLUSH) {
      console.error("[reactive] an effect re-ran more than", MAX_RUNS_PER_FLUSH, "times in one flush (it keeps invalidating itself); skipped until the next change", e.fn);
      e.flags = Flag.Watching; // stays subscribed; the next change notifies it again
      return;
    }
    e.depsTail = undefined;
    e.flags = Flag.Watching | Flag.RecursedCheck;
    const prev = setActiveSub(e);
    try {
      ++cycle;
      ++runDepth;
      e.fn();
    } finally {
      --runDepth;
      activeSub = prev;
      endRun(e);
    }
    finishRun(e);
  } else if (e.deps !== undefined) {
    // only "maybe changed" and nothing did: watch again
    e.flags = Flag.Watching;
  }
}

/** After a run (also when it threw): drop the dependencies it no longer read, or all of them if it disposed itself. */
function endRun(e: EffectNode): void {
  if (e.flags & Flag.Watching) {
    e.flags &= ~Flag.RecursedCheck;
    purgeDeps(e);
  } else {
    // disposed during its own run (Watching is only cleared by dispose while running); reads after that re-linked it
    disposeDeps(e);
    e.flags = Flag.None;
  }
}

/**
 * After a successful run: an effect notified during its own run (a dependency it had already read changed meanwhile)
 * is left Pending/Dirty but unqueued by propagate(). Queue it now; run() re-checks its dependencies and re-runs it
 * only if one really changed.
 */
function finishRun(e: EffectNode): void {
  if (e.flags & Flag.Watching && e.flags & (Flag.Pending | Flag.Dirty)) {
    e.flags &= ~Flag.Recursed;
    enqueue(e);
    if (!batchDepth) flush();
  }
}

function enqueue(e: EffectNode): void {
  e.flags &= ~Flag.Watching; // queued: not notified again until it ran
  queue[queueLength++] = e;
}

/** Run queued effects until the queue is empty. Not re-entrant: a nested call returns, the outer loop continues. */
function flush(): void {
  if (flushing) return;
  flushing = true;
  ++flushId;
  try {
    while (queueHead < queueLength) {
      const e = queue[queueHead]!;
      queue[queueHead++] = undefined;
      run(e);
    }
  } finally {
    // An effect threw: the ones that did not get to run watch again and are re-notified by the next change.
    while (queueHead < queueLength) {
      const e = queue[queueHead]!;
      queue[queueHead++] = undefined;
      e.flags |= Flag.Watching | Flag.Recursed;
    }
    queueHead = queueLength = 0;
    flushing = false;
  }
}

function disposeEffect(e: EffectNode): void {
  e.flags = Flag.None;
  disposeDeps(e);
}

// ---- dependency list helpers ----------------------------------------------------------------------------------------

function disposeDeps(sub: ReactiveNode): void {
  let l = sub.depsTail;
  while (l !== undefined) {
    const prev = l.prevDep;
    unlink(l, sub);
    l = prev;
  }
}

/** Unlink the dependencies after `depsTail`: the ones the last run did not read again. */
function purgeDeps(sub: ReactiveNode): void {
  let l = sub.depsTail !== undefined ? sub.depsTail.nextDep : sub.deps;
  while (l !== undefined) l = unlink(l, sub);
}
