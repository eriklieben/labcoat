// Gallery helpers: section / demo cards, highlighted snippets, forced pseudo-states.
import hljs from "highlight.js/lib/core";
import csharp from "highlight.js/lib/languages/csharp";
import typescript from "highlight.js/lib/languages/typescript";
import { codeBlock, escapeHtml, h, icon, kbd, type Child, type Highlighter, type IconName } from "../src";

hljs.registerLanguage("typescript", typescript);
hljs.registerLanguage("csharp", csharp);

/** The highlighter the gallery passes to codeBlock (the design system itself has no highlight.js dependency). */
export const highlight: Highlighter = (code, lang) => (lang && hljs.getLanguage(lang) ? hljs.highlight(code, { language: lang, ignoreIllegals: true }).value : escapeHtml(code));

export interface SectionInfo {
  id: string;
  title: string;
  icon?: IconName;
  group: "Overview" | "Foundations" | "Components";
}
export const sections: SectionInfo[] = [];

export function section(info: SectionInfo, lead: Child, ...children: Child[]): HTMLElement {
  sections.push(info);
  return h(
    "section",
    { class: "g-section", id: info.id, "aria-labelledby": `${info.id}-h` },
    h("h2", { id: `${info.id}-h` }, info.icon ? icon(info.icon, { size: 22 }) : null, info.title),
    lead ? h("p", { class: "g-lead" }, lead) : null,
    ...children,
  );
}

export interface DemoOptions {
  title: string;
  sub?: Child;
  preview: Child;
  /** usage snippet (TypeScript) */
  code?: string;
  /** keyboard: [keys, what] pairs */
  keys?: [string, string][];
  notes?: Child;
  page?: boolean;
  col?: boolean;
}

export function demo(o: DemoOptions): HTMLElement {
  const notes =
    o.keys || o.notes
      ? h(
          "div",
          { class: "g-notes" },
          o.keys
            ? [
                h("h4", null, "Keyboard"),
                h(
                  "ul",
                  null,
                  o.keys.map(([k, what]) => h("li", null, kbd(k), h("span", null, what))),
                ),
              ]
            : null,
          o.notes ? [h("h4", { style: o.keys ? "margin-top:10px" : undefined }, "Notes"), h("div", null, o.notes)] : null,
        )
      : null;
  return h(
    "article",
    { class: "g-card", "aria-label": o.title },
    h("div", { class: "g-card-head" }, h("h3", null, o.title), o.sub ? h("p", null, o.sub) : null),
    h("div", { class: ["g-preview", o.page && "page", o.col && "col"] }, o.preview),
    o.code || notes
      ? h(
          "div",
          { class: ["g-card-foot", !(o.code && notes) && "single"] },
          o.code ? codeBlock({ code: o.code.trim(), language: "typescript", highlight, wrap: true, label: `${o.title} usage` }) : null,
          notes,
        )
      : null,
  );
}

/** A labelled cell in a state matrix. */
export function state(label: string, ...children: Child[]): HTMLElement {
  return h("div", { class: "g-state" }, h("span", { class: "g-state-label" }, label), ...children);
}

export function row(label: string, ...children: Child[]): HTMLElement {
  return h("div", { class: "g-row" }, h("span", { class: "g-label" }, label), ...children);
}

/** Force the hover / focus / active look on an element (see installPseudoStates). */
export function force<T extends HTMLElement>(el: T, ...states: ("hover" | "focus" | "active")[]): T {
  for (const s of states) el.classList.add(`g-${s}`);
  return el;
}

/**
 * Duplicate every :hover / :focus-visible / :active rule of the loaded stylesheets as a .g-hover / .g-focus /
 * .g-active rule, so the gallery can show those states statically (like Storybook's pseudo-states).
 */
export function installPseudoStates() {
  const map: [RegExp, string][] = [
    [/:hover/g, ".g-hover"],
    [/:focus-visible/g, ".g-focus"],
    [/:active/g, ".g-active"],
  ];
  const test = /:hover|:focus-visible|:active/;
  // Rules live in cascade layers (@layer lc.components { … }): walk the layer blocks too and insert each copy into the
  // same block, so it keeps the original's layer (a copy at the top level would be unlayered and beat everything).
  const visit = (parent: CSSStyleSheet | CSSGroupingRule) => {
    let rules: CSSRuleList;
    try {
      rules = parent.cssRules;
    } catch {
      return;
    }
    const add: string[] = [];
    for (const r of [...rules]) {
      if (typeof CSSLayerBlockRule !== "undefined" && r instanceof CSSLayerBlockRule) {
        visit(r);
        continue;
      }
      if (!(r instanceof CSSStyleRule) || !test.test(r.selectorText)) continue;
      const sel = r.selectorText
        .split(",")
        .filter((s) => test.test(s))
        .map((s) => map.reduce((acc, [re, cls]) => acc.replace(re, cls), s))
        .join(",");
      if (sel) add.push(`${sel} { ${r.style.cssText} }`);
    }
    for (const a of add) {
      try {
        parent.insertRule(a, parent.cssRules.length);
      } catch {
        /* selector the engine rejects: skip */
      }
    }
  };
  for (const sheet of [...document.styleSheets]) visit(sheet);
}
