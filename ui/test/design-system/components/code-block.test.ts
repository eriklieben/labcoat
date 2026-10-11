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
  // the regex the linear scanner replaced (CodeQL js/polynomial-redos): same output on any input
  const reference = (html: string): string[] => {
    const lines: string[] = [];
    const open: string[] = [];
    let cur = "";
    for (const m of html.matchAll(/(<\/?[a-zA-Z][^>]*>)|\n|[^<\n]+/g)) {
      const t = m[0];
      if (t === "\n") {
        lines.push(cur + "</span>".repeat(open.length));
        cur = open.join("");
      } else if (m[1]) {
        if (t.startsWith("</")) open.pop();
        else if (!t.endsWith("/>")) open.push(t);
        cur += t;
      } else cur += t;
    }
    lines.push(cur);
    return lines;
  };

  it("matches the previous regex tokenizer on random and odd inputs", () => {
    const parts = ["<span class=\"a\">", "</span>", "\n", "x", "y z", "<", ">", "</", "<1", "<b/>", "&lt;", "<i>", "</i>", "<a\nb>"];
    let seed = 7;
    const rand = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648);
    const samples = ["", "<", "<<", "<a", "a<", "<<a>", "<a<b>", "</>", "x\n\ny", "<span>\n</span>"];
    for (let n = 0; n < 500; n++) samples.push(Array.from({ length: 1 + Math.floor(rand() * 12) }, () => parts[Math.floor(rand() * parts.length)]).join(""));
    for (const s of samples) expect(splitHtmlLines(s), JSON.stringify(s)).toEqual(reference(s));
  });

  it("stays linear on pathological input", () => {
    const evil = "<a".repeat(50_000); // quadratic for the old regex
    const t0 = performance.now();
    splitHtmlLines(evil);
    expect(performance.now() - t0).toBeLessThan(500);
  });

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
