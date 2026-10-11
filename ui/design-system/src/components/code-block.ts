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
  // A linear scan (no regex backtracking): tags <x…> / </x…>, newlines and text runs. A "<" that doesn't start a
  // complete tag is dropped, as before; highlighter output escapes literal "<" as &lt;.
  const s: SplitState = { html, lines: [], open: [], cur: "", gt: -1 };
  for (let i = 0; i < html.length; ) {
    const c = html[i];
    if (c === "\n") i = splitNewline(s, i);
    else if (c === "<") i = splitTag(s, i);
    else i = splitText(s, i);
  }
  s.lines.push(s.cur);
  return s.lines;
}

interface SplitState {
  readonly html: string;
  readonly lines: string[];
  /** open tags, e.g. '<span class="hljs-string">' */
  readonly open: string[];
  /** the line so far */
  cur: string;
  /** the next ">" at or after the scan position (looked up again only once the scan has passed it: linear) */
  gt: number;
}

/** A newline at `i`: close the open spans, start the next line by reopening them. Returns the next position. */
function splitNewline(s: SplitState, i: number): number {
  s.lines.push(s.cur + "</span>".repeat(s.open.length));
  s.cur = s.open.join("");
  return i + 1;
}

/** A "<" at `i`: a complete tag is copied (and tracked as opened / closed), anything else is dropped. */
function splitTag(s: SplitState, i: number): number {
  const { html } = s;
  if (s.gt < i) s.gt = html.indexOf(">", i);
  if (s.gt < 0 || !isTagStart(html, i)) return i + 1;
  const tag = html.slice(i, s.gt + 1);
  if (tag.startsWith("</")) s.open.pop();
  else if (!tag.endsWith("/>")) s.open.push(tag);
  s.cur += tag;
  return s.gt + 1;
}

/** A text run from `i` up to the next "<" or newline. */
function splitText(s: SplitState, i: number): number {
  const { html } = s;
  let end = i;
  while (end < html.length && html[end] !== "<" && html[end] !== "\n") end++;
  s.cur += html.slice(i, end);
  return end;
}

/** `<` + optional `/` + an ASCII letter at position `i`. */
const isTagStart = (html: string, i: number): boolean => {
  const at = html[i + 1] === "/" ? i + 2 : i + 1;
  const ch = html.codePointAt(at) ?? 0; // past the end: not a letter
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
