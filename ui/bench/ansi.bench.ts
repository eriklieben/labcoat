/**
 * Live console (src/live-output.ts, components/live-console.ts) and ANSI color parsing (src/util.ts `ansi`):
 * streaming chunks into a capped buffer, and rendering lines (appending vs. rebuilding) with ANSI SGR colors.
 */
import { describe, test } from "vitest";
import type { ServerEvent, TestNode } from "../src/contract";
import { LineRenderer } from "../src/components/live-console";
import { appendText, LiveOutputStore, MAX_CHARS, type LiveBuffer } from "../src/live-output";
import { ansi, stripAnsi } from "../src/util";
import { consoleLines, fixed, installManualRaf, QUICK } from "./fixtures";

installManualRaf();

const emptyBuffer = (): LiveBuffer => ({ key: "p|t", lines: [], seq: 0, chars: 0, headLines: 0, omitted: 0, rev: 0, finished: false });

describe("ansi", () => {
  const plain = consoleLines(1_000, 0);
  const colored = consoleLines(1_000, 1);
  const mixed = consoleLines(1_000, 0.3);

  test("parse 1,000 lines", async ({ bench }) => {
    await bench.compare(
      bench("ansi(): plain lines (fast path)", () => plain.map(ansi)),
      bench("ansi(): 30 % colored", () => mixed.map(ansi)),
      bench("ansi(): all colored", () => colored.map(ansi)),
      bench("stripAnsi: all colored", () => colored.map(stripAnsi)),
      QUICK,
    );
  });
});

describe("live console", () => {
  const lines = consoleLines(5_000, 0.3);
  // 5 lines per chunk, the last one without a newline half the time (continued by the next chunk)
  const chunks = Array.from({ length: lines.length / 5 }, (_, i) => lines.slice(i * 5, i * 5 + 5).join("\n") + (i % 2 ? "\n" : ""));

  test("buffer: append chunks", async ({ bench }) => {
    await bench.compare(
      bench("appendText 200 chunks (under the cap)", () => {
        const b = emptyBuffer();
        for (let i = 0; i < 200; i++) appendText(b, { stream: "stdout", text: chunks[i] });
        return b.lines.length;
      }),
      bench(`appendText 1,000 chunks (head + tail cap, ${MAX_CHARS / 1024} K)`, () => {
        const b = emptyBuffer();
        for (const c of chunks) appendText(b, { stream: "stdout", text: c });
        return b.omitted;
      }),
      QUICK,
    );
  });

  test("store: testOutput events", async ({ bench }) => {
    const running = { id: "t", projectId: "p", displayName: "t", outcome: "running", traits: [] } as TestNode;
    let seq = 0;
    const events: ServerEvent[] = chunks.map((text) => ({ type: "testOutput", projectId: "p", id: "t", stream: "stdout", text, seq: 0 }) as ServerEvent);
    const stream = (interleave: boolean) => () => {
      const s = new LiveOutputStore(() => running, undefined, () => {});
      seq = 0;
      for (const e of events) s.apply({ ...e, seq: ++seq, stream: interleave && seq % 3 === 0 ? "stderr" : "stdout" } as ServerEvent);
      return s.get("p|t")?.lines.length;
    };
    await bench.compare(
      bench("LiveOutputStore.apply × 1,000 chunks (stdout)", stream(false)),
      bench("LiveOutputStore.apply × 1,000 chunks (stdout + stderr)", stream(true)),
      QUICK,
    );
  });

  test("render", async ({ bench }) => {
    // 100 chunks ≈ 500 lines (under the 64 K cap, so the buffer's rev stays put and the renderer can append)
    const full = emptyBuffer();
    for (const c of chunks.slice(0, 100)) appendText(full, { stream: "stdout", text: c });
    const at = full.lines.length - 50;
    const el = document.createElement("div");
    let r: LineRenderer;
    await bench.compare(
      bench(`LineRenderer: full render, ${full.lines.length} lines`, () => new LineRenderer(document.createElement("div")).render(full)),
      bench(
        `LineRenderer: append 50 lines to ${at}`,
        {
          beforeEach() {
            el.replaceChildren();
            r = new LineRenderer(el);
            r.render({ ...full, lines: full.lines.slice(0, at) });
          },
        },
        () => r.render(full),
      ),
      fixed(100),
    );
  });
});
