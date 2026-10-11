import { describe, expect, it } from "vitest";
import { root, signal } from "../../../design-system/src/core";
import { codeBlock, escapeHtml, splitHtmlLines, type Highlighter } from "../../../design-system/src/components/code-block";

const lines = (el: HTMLElement) => [...el.querySelectorAll<HTMLElement>(".lc-code-line")];

describe("escapeHtml", () => {
  it("escapes &, < and >", () => {
    expect(escapeHtml("a < b && c > d")).toBe("a &lt; b &amp;&amp; c &gt; d");
  });
});

describe("splitHtmlLines", () => {
  it("closes and reopens spans that cross line breaks", () => {
    expect(splitHtmlLines('a<span class="s">"x\ny"</span>b\nc')).toEqual(['a<span class="s">"x</span>', '<span class="s">y"</span>b', "c"]);
  });

  it("handles nesting, self-closing tags and empty lines", () => {
    expect(splitHtmlLines('<span class="a"><span class="b">1\n2</span>3</span>\n\n<br/>')).toEqual([
      '<span class="a"><span class="b">1</span></span>',
      '<span class="a"><span class="b">2</span>3</span>',
      "",
      "<br/>",
    ]);
  });
});

describe("codeBlock()", () => {
  it("renders a focusable region with one span per line (escaped, CRLF normalized, trailing newline dropped)", () => {
    const el = codeBlock({ code: "a<b\r\n\r\nc\n", label: "Source", language: "csharp", lineNumbers: true, wrap: true, startLine: 40, markLines: [41], class: "x", maxHeight: 200 });
    expect(el.className).toBe("lc-code numbered wrap x");
    expect(el.getAttribute("role")).toBe("region");
    expect(el.getAttribute("aria-label")).toBe("Source");
    expect(el.tabIndex).toBe(0);
    expect(el.style.getPropertyValue("max-height")).toBe("200px");
    expect(el.style.getPropertyValue("--lc-code-digits")).toBe("2");
    expect(el.querySelector("pre.lc-code-pre > code")!.className).toBe("hljs language-csharp");
    const ls = lines(el);
    expect(ls.map((l) => l.dataset.n)).toEqual(["40", "41", "42"]);
    expect(ls[0].innerHTML).toBe("a&lt;b");
    expect(ls[1].textContent).toBe("​"); // empty lines keep their height
    expect(ls[1].classList.contains("marked")).toBe(true);
    expect(ls[0].classList.contains("marked")).toBe(false);
  });

  it("uses the highlighter's HTML and re-renders when code or marks change", () => {
    const calls: [string, string | undefined][] = [];
    const hl: Highlighter = (code, lang) => {
      calls.push([code, lang]);
      return `<span class="hljs-keyword">${escapeHtml(code)}</span>`;
    };
    root((dispose) => {
      const code = signal("var x\nvar y");
      const marks = signal<readonly number[]>([]);
      const el = codeBlock({ code, label: "L", language: "js", highlight: hl, markLines: marks, maxHeight: "50vh" });
      expect(el.className).toBe("lc-code");
      expect(el.style.getPropertyValue("max-height")).toBe("50vh");
      expect(lines(el).map((l) => l.innerHTML)).toEqual(['<span class="hljs-keyword">var x</span>', '<span class="hljs-keyword">var y</span>']);
      expect(calls).toEqual([["var x\nvar y", "js"]]);
      marks.set([2]);
      expect(lines(el)[1].classList.contains("marked")).toBe(true);
      code.set(Array.from({ length: 12 }, (_, i) => `l${i}`).join("\n"));
      expect(lines(el)).toHaveLength(12);
      expect(el.style.getPropertyValue("--lc-code-digits")).toBe("2");
      dispose();
    });
    expect(codeBlock({ code: "x", label: "y" }).querySelector("code")!.className).toBe("hljs");
  });
});
