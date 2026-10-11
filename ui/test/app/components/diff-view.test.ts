import { describe, expect, it } from "vitest";
import { root } from "@eriklieben/labcoat-design-system";
import { assertionDiff, failureMessage } from "../../../src/components/diff-view";
import { text } from "../../helpers/app";

const inRoot = <T>(fn: () => T): T => root(() => fn());

describe("failureMessage()", () => {
  it("shows a message it can't parse as the raw text", () => {
    const el = inRoot(() => failureMessage("Something went wrong"));
    expect(el.tagName).toBe("PRE");
    expect(el.className).toBe("mb-message");
    expect(el.textContent).toBe("Something went wrong");
  });

  it("MSTest object form: summary, inline expected/actual diff and a Raw message disclosure", () => {
    const msg = "Assert.AreEqual failed. Expected:<10>. Actual:<19>. 'expected' expression: 'x', 'actual' expression: 'y'.";
    const el = inRoot(() => failureMessage(msg));
    expect(el.className).toBe("failure-message");
    expect(text(el.querySelector(":scope > pre.mb-message"))).toBe("Assert.AreEqual failed.");
    const block = el.querySelector(".diff-block")!;
    expect(block.classList.contains("diff-kind-inline")).toBe(true);
    expect(text(block.querySelector(".diff-value.exp"))).toBe("10");
    expect(text(block.querySelector(".diff-value.act"))).toBe("19");
    // the changed characters are marked
    expect(block.querySelectorAll("mark").length).toBeGreaterThan(0);
    // no head for an inline, untruncated diff
    expect(block.querySelector(".diff-head")).toBeNull();

    const toggle = el.querySelector<HTMLButtonElement>(".raw-toggle")!;
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(el.querySelector(".raw-message")).toBeNull();
    toggle.click();
    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    expect(el.querySelector(".raw-message")!.textContent).toBe(msg);
    toggle.click();
    expect(el.querySelector(".raw-message")).toBeNull();
  });

  it("omits the summary block when the message is only expected/actual", () => {
    const el = inRoot(() => failureMessage("Expected: 1\nActual:   2"));
    expect(el.querySelector(":scope > pre.mb-message")).toBeNull();
    expect(text(el.querySelector(".diff-value.exp"))).toBe("1");
  });
});

describe("assertionDiff()", () => {
  it("quoted values get quotes; an empty value says (empty)", () => {
    const el = inRoot(() => assertionDiff({ expected: "", actual: "abc", summary: "", truncated: false, quoted: true }));
    const exp = el.querySelector(".diff-value.exp")!;
    expect(exp.querySelectorAll(".diff-quote")).toHaveLength(2);
    expect(text(exp.querySelector(".diff-empty"))).toBe("(empty)");
  });

  it("partial (cut) values show ellipses and a 'Partial values' note", () => {
    const el = inRoot(() =>
      assertionDiff({
        expected: "hello world",
        actual: "hello there",
        summary: "",
        truncated: true,
        quoted: true,
        cut: { expected: { start: true, end: true }, actual: { start: false, end: true } },
      }),
    );
    expect(el.querySelector(".diff-head .diff-note")!.textContent).toContain("Partial values");
    expect(el.querySelector(".diff-value.exp")!.querySelectorAll(".diff-cut")).toHaveLength(2);
    expect(el.querySelector(".diff-value.act")!.querySelectorAll(".diff-cut")).toHaveLength(1);
  });

  it("changed whitespace is made visible", () => {
    const el = inRoot(() => assertionDiff({ expected: "a b", actual: "a  b", summary: "", truncated: false, quoted: false }));
    const marks = [...el.querySelectorAll("mark")].map((m) => m.textContent);
    expect(marks.some((m) => /·/.test(m ?? ""))).toBe(true);
  });

  it("multi-line values: a line diff (list) with legend, stats and per-line expected/actual labels", () => {
    const el = inRoot(() => assertionDiff({ expected: "one\ntwo\nthree", actual: "one\nTWO\nthree\nfour", summary: "", truncated: false, quoted: false }));
    expect(el.classList.contains("diff-kind-lines")).toBe(true);
    const head = el.querySelector(".diff-head")!;
    expect(head.querySelector(".diff-legend.exp")).not.toBeNull();
    expect(head.querySelector(".diff-tag")).toBeNull();
    expect(text(head.querySelector(".diff-stats"))).toMatch(/−1 \+2/);
    const listEl = el.querySelector(".diff-lines")!;
    expect(listEl.getAttribute("role")).toBe("list");
    const rows = [...listEl.querySelectorAll(".diff-line")];
    expect(rows.every((r) => r.getAttribute("role") === "listitem")).toBe(true);
    expect(rows.some((r) => r.classList.contains("exp") && r.textContent!.includes("expected: "))).toBe(true);
    expect(rows.some((r) => r.classList.contains("act") && r.textContent!.includes("actual: "))).toBe(true);
  });

  it("JSON values are pretty-printed and tagged", () => {
    const el = inRoot(() => assertionDiff({ expected: '{"a":1,"b":2}', actual: '{"a":1,"b":3}', summary: "", truncated: false, quoted: false }));
    expect(el.querySelector(".diff-tag")!.textContent).toBe("JSON");
    expect(el.querySelectorAll(".diff-line").length).toBeGreaterThan(2);
  });

  it("long unchanged stretches fold and expand on click", () => {
    const base = Array.from({ length: 12 }, (_, i) => `line ${i}`);
    const actual = [...base.slice(0, 11), "changed"];
    const el = inRoot(() => assertionDiff({ expected: base.join("\n"), actual: actual.join("\n"), summary: "", truncated: false, quoted: false }));
    const fold = el.querySelector<HTMLButtonElement>(".diff-fold")!;
    expect(fold.textContent).toMatch(/8 unchanged lines/);
    const before = el.querySelectorAll(".diff-line").length;
    fold.click();
    expect(el.querySelector(".diff-fold")).toBeNull();
    expect(el.querySelectorAll(".diff-line")).toHaveLength(before + 8);
  });

  it("short unchanged stretches between changes (≤ 2 lines outside the context) are not folded", () => {
    const exp = ["a", "1", "2", "3", "4", "5", "6", "7", "z"];
    const act = ["A", "1", "2", "3", "4", "5", "6", "7", "Z"];
    const el = inRoot(() => assertionDiff({ expected: exp.join("\n"), actual: act.join("\n"), summary: "", truncated: false, quoted: false }));
    // 7 equal lines between two changes with 3 context each → 1 left: shown, not folded (≤ 2)
    expect(el.querySelector(".diff-fold")).toBeNull();
    expect(el.querySelectorAll(".diff-line").length).toBeGreaterThanOrEqual(11);
  });
});
