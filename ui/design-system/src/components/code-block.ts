/**
 * Code block: monospace, optional line numbers, marked lines, optional syntax highlighting through a highlighter
 * you pass in (the design system has no highlighter dependency):
 *
 *   import hljs from "highlight.js/lib/core";
 *   const highlight: Highlighter = (code, lang) => (lang ? hljs.highlight(code, { language: lang }).value : escapeHtml(code));
 *   codeBlock({ code: () => source(), language: "csharp", highlight, lineNumbers: true, startLine: 40, markLines: [42],
 *     label: "Source of CalculatorTests.cs" })
 *
 * Markup: `<div class="lc-code [numbered] [wrap]" role="region" aria-label tabindex="0"><pre><code class="hljs">
 * <span class="lc-code-line [marked]" data-n="40">…</span>…</code></pre></div>`. The block is focusable so keyboard
 * users can scroll it. Line numbers are CSS-generated (never copied with the text). The highlighter must return
 * HTML-escaped markup (highlight.js does); spans that cross line breaks are split per line.
 */
import { effect, h, read, type ClassValue, type MaybeReactive } from "../core";

/** (code, language) → escaped HTML with <span class="hljs-…"> tokens */
export type Highlighter = (code: string, language?: string) => string;

export const escapeHtml = (s: string) => s.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");

/** Split highlighted HTML into one well-formed HTML string per line (closing and reopening spans at newlines). */
export function splitHtmlLines(html: string): string[] {
  const lines: string[] = [];
  const open: string[] = []; // open tags, e.g. '<span class="hljs-string">'
  let cur = "";
  // A linear scan (no regex backtracking): tags <x…> / </x…>, newlines and text runs. A "<" that doesn't start a
  // complete tag is dropped, as before; highlighter output escapes literal "<" as &lt;.
  let gt = -1; // the next ">" at or after i (looked up again only once i has passed it, so the scan stays linear)
  for (let i = 0; i < html.length; ) {
    const c = html[i];
    if (c === "\n") {
      lines.push(cur + "</span>".repeat(open.length));
      cur = open.join("");
      i++;
    } else if (c === "<") {
      if (gt < i) gt = html.indexOf(">", i);
      if (gt < 0 || !isTagStart(html, i)) {
        i++;
        continue;
      }
      const tag = html.slice(i, gt + 1);
      if (tag.startsWith("</")) open.pop();
      else if (!tag.endsWith("/>")) open.push(tag);
      cur += tag;
      i = gt + 1;
    } else {
      let end = i;
      while (end < html.length && html[end] !== "<" && html[end] !== "\n") end++;
      cur += html.slice(i, end);
      i = end;
    }
  }
  lines.push(cur);
  return lines;
}

/** `<` + optional `/` + an ASCII letter at position `i`. */
const isTagStart = (html: string, i: number): boolean => {
  const at = html[i + 1] === "/" ? i + 2 : i + 1;
  const ch = html.charCodeAt(at);
  return (ch >= 65 && ch <= 90) || (ch >= 97 && ch <= 122);
};

export interface CodeBlockOptions {
  code: MaybeReactive<string>;
  /** accessible name of the scrollable region */
  label: string;
  language?: string;
  highlight?: Highlighter;
  lineNumbers?: boolean;
  /** number of the first line (default 1) */
  startLine?: number;
  /** absolute line numbers to highlight */
  markLines?: MaybeReactive<readonly number[]>;
  /** soft-wrap long lines instead of scrolling horizontally */
  wrap?: boolean;
  maxHeight?: number | string;
  class?: ClassValue;
}

export function codeBlock(opts: CodeBlockOptions): HTMLDivElement {
  const { label, language, highlight, lineNumbers = false, startLine = 1, wrap = false } = opts;
  const code = h("code", { class: ["hljs", language && `language-${language}`] });
  const marks = () => new Set(read(opts.markLines) ?? []);
  const render = () => {
    const src = read(opts.code).replace(/\r\n?/g, "\n").replace(/\n$/, "");
    const html = highlight ? highlight(src, language) : escapeHtml(src);
    const m = marks();
    code.innerHTML = splitHtmlLines(html)
      .map((l, i) => {
        const n = startLine + i;
        return `<span class="lc-code-line${m.has(n) ? " marked" : ""}" data-n="${n}">${l || "\u200b"}</span>`;
      })
      .join("");
  };
  effect(render); // re-renders when the code or the marked lines change
  return h(
    "div",
    {
      class: ["lc-code", lineNumbers && "numbered", wrap && "wrap", opts.class],
      role: "region",
      "aria-label": label,
      tabIndex: 0,
      style: {
        "max-height": typeof opts.maxHeight === "number" ? `${opts.maxHeight}px` : opts.maxHeight,
        "--lc-code-digits": () => String(String(startLine + read(opts.code).split("\n").length).length),
      },
    },
    h("pre", { class: "lc-code-pre" }, code),
  );
}
