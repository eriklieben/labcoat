import { describe, expect, it } from "vitest";
import { buildDiff, charDiff, diffSeq, foldContext, lineDiff, parseAssertion, prettyJson, stripExpressions, type DiffLine } from "../../src/diff";

describe("stripExpressions", () => {
  const optional = /\s*'expected' expression: '.*?', 'actual' expression: '.*?'\.?/s;
  const required = /\s*'expected' expression: '.*?', 'actual' expression: '.*?'\./s;
  it("removes the expression suffix like the patterns it replaces", () => {
    for (const s of [
      "msg 'expected' expression: 'a', 'actual' expression: 'b'. more",
      "msg\n 'expected' expression: 'a', 'actual' expression: 'b' more",
      "'expected' expression: 'a', 'actual' expression: 'b'",
      "'expected' expression: 'a', 'actual' expression: 'b' x'. y",
      "'expected' expression: 'a'",
      "'expected' expression: 'a', 'actual' expression: 'b",
      "nothing",
    ]) {
      expect(stripExpressions(s, "optional")).toBe(s.replace(optional, ""));
      expect(stripExpressions(s, "required")).toBe(s.replace(required, ""));
    }
  });
});

describe("parseAssertion", () => {
  it("undefined for empty / unrecognized messages", () => {
    expect(parseAssertion(undefined)).toBeUndefined();
    expect(parseAssertion("")).toBeUndefined();
    expect(parseAssertion("Something broke")).toBeUndefined();
    expect(parseAssertion("Expected: 1\nnothing")).toBeUndefined();
  });

  it("MSTest object form with expression suffix", () => {
    const p = parseAssertion("Assert.AreEqual failed. Expected:<10>. Actual:<9>. 'expected' expression: 'a', 'actual' expression: 'b'.");
    expect(p).toEqual({ expected: "10", actual: "9", summary: "Assert.AreEqual failed.", truncated: false, quoted: false });
  });

  it("MSTest object form with a user message and multi-line values", () => {
    const p = parseAssertion("Assert.AreEqual failed. Expected:<a\r\nb>. Actual:<a\r\nc>. Totals differ");
    expect(p).toMatchObject({ expected: "a\nb", actual: "a\nc", summary: "Assert.AreEqual failed. Totals differ" });
    const end = parseAssertion("Assert.AreEqual failed. Expected:<1>. Actual:<2>.");
    expect(end).toMatchObject({ expected: "1", actual: "2", summary: "Assert.AreEqual failed." });
  });

  it("MSTest object form without a closing actual falls through to the line form", () => {
    expect(parseAssertion("Expected:<1>. Actual:<2")).toBeUndefined();
  });

  it("NUnit / MSTest 4 string form with caret and truncation; control pictures restored", () => {
    const msg = ['Assert.That(status, Is.EqualTo("sent"))', '  Expected: "...sent␊ok..."', '  But was:  "queued"', "  -----------^", "User message"].join("\n");
    const p = parseAssertion(msg)!;
    expect(p.expected).toBe("sent\nok");
    expect(p.actual).toBe("queued");
    expect(p.quoted).toBe(true);
    expect(p.truncated).toBe(true);
    expect(p.cut).toEqual({ expected: { start: true, end: true }, actual: { start: false, end: false } });
    expect(p.summary).toBe('Assert.That(status, Is.EqualTo("sent"))\nUser message');
  });

  it("xUnit form with ↓/↑ marker lines", () => {
    const msg = ["Assert.Equal() Failure: Strings differ", "           ↓ (pos 2)", "Expected: \"abc\"", "Actual:   \"abd\"", "           ↑ (pos 2)"].join("\n");
    const p = parseAssertion(msg)!;
    expect(p).toMatchObject({ expected: "abc", actual: "abd", summary: "Assert.Equal() Failure: Strings differ", truncated: false, quoted: true });
    const n = parseAssertion("Expected: 10\nActual:   9")!;
    expect(n).toMatchObject({ expected: "10", actual: "9", quoted: false });
  });

  it("strips the MSTest expression suffix from line-form summaries", () => {
    const p = parseAssertion("Assert.AreEqual failed. 'expected' expression: 'x', 'actual' expression: 'y'.\nExpected: 1\nActual: 2")!;
    expect(p.summary).toBe("Assert.AreEqual failed.");
  });
});

describe("diffSeq", () => {
  it("trims common prefix/suffix and emits an LCS edit script", () => {
    const e = diffSeq("abcxdef".split(""), "abcydef".split(""));
    expect(e.map((x) => `${x.op}:${x.value}`).join(" ")).toBe("eq:a eq:b eq:c del:x ins:y eq:d eq:e eq:f");
  });

  it("pure insertions / deletions and identical input", () => {
    expect(diffSeq([], [1, 2])).toEqual([{ op: "ins", value: 1 }, { op: "ins", value: 2 }]);
    expect(diffSeq([1, 2], [])).toEqual([{ op: "del", value: 1 }, { op: "del", value: 2 }]);
    expect(diffSeq([1], [1])).toEqual([{ op: "eq", value: 1 }]);
  });

  it("finds the longest common subsequence in the middle", () => {
    const e = diffSeq(["a", "x", "b", "y"], ["q", "a", "b", "z"]);
    expect(e.filter((x) => x.op === "eq").map((x) => x.value)).toEqual(["a", "b"]);
  });

  it("uses a custom equality", () => {
    const e = diffSeq(["A"], ["a"], (x, y) => x.toLowerCase() === y.toLowerCase());
    expect(e).toEqual([{ op: "eq", value: "A" }]);
  });

  it("falls back to replace-all for huge inputs", () => {
    const a = Array.from({ length: 2100 }, (_, i) => `a${i}`);
    const b = Array.from({ length: 2100 }, (_, i) => `b${i}`);
    const e = diffSeq(a, b);
    expect(e).toHaveLength(4200);
    expect(e[0].op).toBe("del");
    expect(e[2100].op).toBe("ins");
  });
});

describe("charDiff", () => {
  it("word diff folds tiny equal islands between changes", () => {
    const d = charDiff("status sent", "status queued");
    expect(d.expected).toEqual([{ text: "status ", changed: false }, { text: "sent", changed: true }]);
    expect(d.actual).toEqual([{ text: "status ", changed: false }, { text: "queued", changed: true }]);
  });

  it("char diff marks single characters", () => {
    const d = charDiff("abcd", "abXd", "char");
    expect(d.expected).toEqual([{ text: "ab", changed: false }, { text: "c", changed: true }, { text: "d", changed: false }]);
    expect(d.actual).toEqual([{ text: "ab", changed: false }, { text: "X", changed: true }, { text: "d", changed: false }]);
  });

  it("a short equal run between changes becomes part of both sides' change", () => {
    const d = charDiff("aXbY", "cXdY", "char");
    expect(d.expected.map((s) => [s.text, s.changed])).toEqual([["aXb", true], ["Y", false]]);
    expect(d.actual.map((s) => [s.text, s.changed])).toEqual([["cXd", true], ["Y", false]]);
  });
});

describe("lineDiff / foldContext", () => {
  it("numbers lines and pairs similar changed lines with intra-line segments", () => {
    const l = lineDiff("one\ntwo apples\nthree", "one\ntwo pears\nthree\nfour");
    expect(l.map((x) => [x.op, x.text, x.a, x.b])).toEqual([
      ["eq", "one", 1, 1],
      ["del", "two apples", 2, undefined],
      ["ins", "two pears", undefined, 2],
      ["eq", "three", 3, 3],
      ["ins", "four", undefined, 4],
    ]);
    expect(l[1].segments?.some((s) => s.changed)).toBe(true);
    expect(l[2].segments?.find((s) => s.changed)?.text).toBe("pears");
    expect(l[4].segments).toBeUndefined();
  });

  it("dissimilar lines get no segments; empty lines pair with each other", () => {
    const l = lineDiff("aaaa\n", "zzzz\n");
    expect(l.find((x) => x.op === "del")?.segments).toBeUndefined();
    const e = lineDiff("x\n\ny", "x\nq\ny");
    expect(e.map((x) => x.op)).toEqual(["eq", "del", "ins", "eq"]);
  });

  it("foldContext collapses long equal stretches but keeps short ones", () => {
    const lines: DiffLine[] = Array.from({ length: 20 }, (_, i) => ({ op: "eq" as const, text: `l${i}`, a: i + 1, b: i + 1 }));
    lines[10] = { op: "del", text: "x", a: 11 };
    const f = foldContext(lines, 3);
    expect(f[0]).toEqual({ fold: 7, from: 0 });
    expect(f.at(-1)).toEqual({ fold: 6, from: 14 });
    expect(f.filter((x) => "op" in x)).toHaveLength(7);
    const short: DiffLine[] = [{ op: "eq", text: "a" }, { op: "eq", text: "b" }, { op: "del", text: "c" }];
    expect(foldContext(short, 0)).toEqual(short);
  });
});

describe("prettyJson / buildDiff", () => {
  it("prettyJson only when both sides are JSON objects/arrays", () => {
    expect(prettyJson('{"a":1}', "[1]")).toEqual({ expected: '{\n  "a": 1\n}', actual: "[\n  1\n]" });
    expect(prettyJson('{"a":1}', "nope")).toBeUndefined();
    expect(prettyJson("{bad", "{}")).toBeUndefined();
  });

  it("JSON → line diff with counts", () => {
    const m = buildDiff('{"a":1,"b":2}', '{"a":1,"b":3}');
    expect(m.kind).toBe("lines");
    if (m.kind !== "lines") return;
    expect(m.json).toBe(true);
    expect([m.added, m.removed]).toEqual([1, 1]);
  });

  it("short single values → inline char diff; sentences → word diff", () => {
    const g = buildDiff("3f2a-11", "3f2b-11");
    expect(g.kind).toBe("inline");
    const w = buildDiff("the quick fox", "the slow fox");
    expect(w).toMatchObject({ kind: "inline", expected: [{ text: "the ", changed: false }, { text: "quick", changed: true }, { text: " fox", changed: false }] });
  });

  it("cut values: the unknown tail on the other side is not marked as changed", () => {
    const cut = { expected: { start: false, end: true }, actual: { start: false, end: false } };
    const m = buildDiff("abc", "abcdef", cut);
    if (m.kind !== "inline") throw new Error("inline expected");
    expect(m.actual.at(-1)!.changed).toBe(false);
    const m2 = buildDiff("abcdef", "abc", { expected: { start: false, end: false }, actual: { start: false, end: true } });
    if (m2.kind !== "inline") throw new Error("inline expected");
    expect(m2.expected.at(-1)!.changed).toBe(false);
  });

  it("multi-line or long values → plain line diff", () => {
    const m = buildDiff("a\nb", "a\nc");
    expect(m).toMatchObject({ kind: "lines", json: false, added: 1, removed: 1 });
    const long = buildDiff("x".repeat(200), "y".repeat(200));
    expect(long.kind).toBe("lines");
  });
});

describe("parseAssertion in other UI languages", () => {
  it("reads MSTest's object form with localized labels", () => {
    const de = parseAssertion('Fehler bei "Assert.AreEqual". Erwartet:<1>. Tatsächlich:<2>. „expected“ Ausdruck: „1“, „actual“ Ausdruck: „2“.')!;
    expect(de).toMatchObject({ expected: "1", actual: "2", quoted: false, truncated: false });
    expect(de.summary).toContain('Fehler bei "Assert.AreEqual".');
    const pl = parseAssertion("Oczekiwana:<Order #42\n---\nTotal 9>. Rzeczywista:<Order #42\n---\nTotal 10>. note")!;
    expect(pl.expected).toBe("Order #42\n---\nTotal 9");
    expect(pl.actual).toBe("Order #42\n---\nTotal 10");
    expect(parseAssertion("期待値:<1>。実際の値:<2>。")).toMatchObject({ expected: "1", actual: "2" });
    expect(parseAssertion("Erwartet:<a> und dann <b>. Tatsächlich:<c>.")).toMatchObject({ expected: "a> und dann <b", actual: "c" });
  });

  it("reads MSTest 4's string form with localized labels and its caret line", () => {
    const p = parseAssertion('Fehler bei "Assert.AreEqual". Die erwartete Länge der Zeichenfolge ist 4, war aber 6. Confirmation email was not dispatched\r\nErwartet:  "sent"\r\nWar aber:  "queued"\r\n------------^')!;
    expect(p).toMatchObject({ expected: "sent", actual: "queued", quoted: true });
    expect(p.summary).toContain("Confirmation email was not dispatched");
    expect(parseAssertion('Erwartet:  "...ab"\nWar aber:  "...ac..."\n-----^')).toMatchObject({ expected: "ab", actual: "ac", truncated: true });
  });

  it("leaves other messages alone", () => {
    expect(parseAssertion("Value:<1> without a second part")).toBeUndefined();
    expect(parseAssertion("Ein <b>:<x> Text.")).toBeUndefined();
    expect(parseAssertion("a: 1\nb: 2\nno caret")).toBeUndefined();
    expect(parseAssertion("a: 1\n<b>: 2\n---^")).toBeUndefined();
    expect(parseAssertion("a: 1\nb:\n---^")).toBeUndefined();
    expect(parseAssertion("Erwartet:<1>. Tatsächlich:<2")).toBeUndefined();
    expect(parseAssertion(":<1>. B:<2>.")).toBeUndefined();
  });
});
