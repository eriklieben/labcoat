/**
 * Global test setup (happy-dom). Stubs the browser APIs happy-dom lacks or implements as no-ops, in a deterministic,
 * test-controllable form (see test/helpers/dom.ts for the controls):
 *   - Element.animate / getAnimations: FakeAnimation (stays "running" until finishAnimations() or .finish())
 *   - matchMedia: answers from a mutable table (setMedia(query, matches) fires "change")
 *   - document.startViewTransition: runs the callback synchronously, resolved promises
 *   - ResizeObserver / IntersectionObserver: recorded instances, triggered with triggerResize()/triggerIntersection()
 *   - <canvas>.getContext("2d"): a recording no-op 2D context (confetti)
 *   - WebSocket: FakeWebSocket (no network; open()/message()/close() driven by the test)
 *   - navigator.clipboard.writeText: resolves, records the text
 *   - fetch: rejects unless a test stubs it (no network)
 * After each test: DOM, <html> attributes, storage, timers and the stubs' state are reset.
 */
import { afterEach, beforeEach, vi } from "vitest";
import { resetDomStubs, installDomStubs } from "./helpers/dom";

installDomStubs();

beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn(async (url: unknown) => {
    throw new Error(`unexpected fetch in a unit test: ${String(url)} (stub it with vi.stubGlobal("fetch", …))`);
  }));
});

afterEach(async () => {
  vi.useRealTimers();
  document.body.replaceChildren();
  // element disposal on disconnect is deferred by a microtask
  await Promise.resolve();
  const html = document.documentElement;
  for (const a of [...html.attributes]) if (a.name !== "lang") html.removeAttribute(a.name);
  try {
    localStorage.clear();
    sessionStorage.clear();
  } catch {
    /* ignore */
  }
  resetDomStubs();
});
