/**
 * Expected/actual diffs of failed assertions and snapshot files (src/diff.ts, components/diff-view.ts): message
 * parsing, word/char diffs of single values, LCS line diffs of multi-line text and pretty-printed JSON, context folding,
 * and the DOM the detail pane builds from them.
 */
import { describe, test } from "vitest";
import { buildDiff, charDiff, foldContext, lineDiff, parseAssertion, prettyJson } from "../src/diff";
import { failureMessage, lineDiffView } from "../src/components/diff-view";
import { root } from "@eriklieben/labcoat-design-system/core";
import { jsonDoc, mutateLines, QUICK, textLines } from "./fixtures";

const MSTEST_OBJECT = "Assert.AreEqual failed. Expected:<Order { Id = 42, Total = 99.95, Status = Shipped }>. Actual:<Order { Id = 42, Total = 99.59, Status = Pending }>. 'expected' expression: 'expected', 'actual' expression: 'order'. Order total mismatch";
const NUNIT_STRING = 'Assert.AreEqual failed. Expected string length 38 but was 37. Confirmation email was not dispatched\nExpected: "The quick brown fox jumps over the dog."\nBut was:  "The quick brown fox jumped over the dog"\n-----------------------------------^';
const GUID_A = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
const GUID_B = "3f2504e0-4f89-11d3-9a0c-0305e82c3310";
const SENTENCE_A = "The order 1042 for customer 77 was shipped to 1 Main Street, Springfield on 2026-10-01 with 3 items";
const SENTENCE_B = "The order 1042 for customer 77 was queued for 1 Main St, Springfield on 2026-10-02 with 4 items";

/** Builds the element in a root that is disposed right after (its bindings would otherwise live on). */
const built = (make: () => HTMLElement) => () =>
  root((dispose) => {
    make();
    dispose();
  });

const text = (n: number) => {
  const a = textLines(n);
  return { a: a.join("\n"), b: mutateLines(a).join("\n") };
};

const json = (orders: number) => {
  const doc = jsonDoc(orders);
  const changed = structuredClone(doc);
  changed.orders.forEach((o, i) => {
    if (i % 7 === 0) o.status = o.status === "shipped" ? "pending" : "shipped";
    if (i % 11 === 0) o.lines.push({ sku: `SKU-${i}-x`, qty: 1, price: 1 });
  });
  return { a: JSON.stringify(doc), b: JSON.stringify(changed) };
};

describe("diff", () => {
  test("assertion messages", async ({ bench }) => {
    await bench.compare(
      bench("parseAssertion: MSTest object form", () => parseAssertion(MSTEST_OBJECT)),
      bench("parseAssertion: NUnit/MSTest 4 string form", () => parseAssertion(NUNIT_STRING)),
      bench("parseAssertion: no match (plain message)", () => parseAssertion("System.NullReferenceException: Object reference not set to an instance of an object.")),
      QUICK,
    );
  });

  test("single values (inline diff)", async ({ bench }) => {
    await bench.compare(
      bench("charDiff GUID (char)", () => charDiff(GUID_A, GUID_B, "char")),
      bench("charDiff sentence (word)", () => charDiff(SENTENCE_A, SENTENCE_B, "word")),
      bench("buildDiff sentence", () => buildDiff(SENTENCE_A, SENTENCE_B)),
      QUICK,
    );
  });

  test("multi-line text (line diff)", async ({ bench }) => {
    const t200 = text(200);
    const t1k = text(1_000);
    const t2k = text(2_000);
    const t5k = text(5_000); // above the LCS cell limit: the fallback path
    await bench.compare(
      bench("lineDiff 200 lines, 5 % edited", () => lineDiff(t200.a, t200.b)),
      bench("lineDiff 1,000 lines, 5 % edited", () => lineDiff(t1k.a, t1k.b)),
      bench("lineDiff 2,000 lines, 5 % edited", () => lineDiff(t2k.a, t2k.b)),
      bench("lineDiff 5,000 lines (over the LCS limit)", () => lineDiff(t5k.a, t5k.b)),
      { ...QUICK, iterations: 5 },
    );
  });

  test("JSON (pretty-print + line diff)", async ({ bench }) => {
    const j20 = json(20); // ~250 lines pretty-printed
    const j150 = json(150); // ~2,000 lines
    await bench.compare(
      bench("prettyJson, 20 orders", () => prettyJson(j20.a, j20.b)),
      bench("buildDiff JSON, 20 orders (~250 lines)", () => buildDiff(j20.a, j20.b)),
      bench("buildDiff JSON, 150 orders (~2,000 lines)", () => buildDiff(j150.a, j150.b)),
      { ...QUICK, iterations: 5 },
    );
  });

  test("context folding + DOM", async ({ bench }) => {
    const t2k = text(2_000);
    const lines2k = lineDiff(t2k.a, t2k.b);
    const t200 = text(200);
    const lines200 = lineDiff(t200.a, t200.b);
    await bench.compare(
      bench("foldContext, 2,000-line diff", () => foldContext(lines2k)),
      bench("lineDiffView DOM, 200-line diff", built(() => lineDiffView(lines200))),
      bench("lineDiffView DOM, 2,000-line diff", built(() => lineDiffView(lines2k))),
      bench("failureMessage DOM (MSTest object form)", built(() => failureMessage(MSTEST_OBJECT))),
      bench("failureMessage DOM (string form)", built(() => failureMessage(NUNIT_STRING))),
      QUICK,
    );
  });
});
