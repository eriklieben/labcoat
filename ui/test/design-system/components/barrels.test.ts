import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import * as pkg from "../../../design-system/src";
import * as components from "../../../design-system/src/components";
import * as core from "../../../design-system/src/core";
import * as viaAlias from "@eriklieben/labcoat-design-system";

const readme = readFileSync(resolve(import.meta.dirname, "../../../design-system/README.md"), "utf8");

/** The `Import` column of README "Available primitives" plus its "Helpers:" line. */
function documentedExports(): string[] {
  const section = readme.slice(readme.indexOf("## Available primitives"), readme.indexOf("\n## ", readme.indexOf("## Available primitives") + 5));
  const names = new Set<string>();
  for (const line of section.split("\n")) {
    if (line.startsWith("|") && !line.startsWith("|---") && !line.startsWith("| Primitive")) {
      const col = line.split("|")[2] ?? "";
      for (const m of col.matchAll(/`([A-Za-z]\w*)`/g)) names.add(m[1]);
    }
    if (line.startsWith("Helpers:")) for (const m of line.matchAll(/`([A-Za-z]\w*)`/g)) names.add(m[1]);
  }
  return [...names];
}

describe("package exports", () => {
  it("exports every primitive and helper the README lists", () => {
    const names = documentedExports().filter((n) => n !== "Highlighter" && n !== "exit"); // a type; an option of portal()
    expect(names.length).toBeGreaterThan(40);
    const missing = names.filter((n) => !(n in pkg));
    expect(missing).toEqual([]);
  });

  it("./core exports the reactive core, h() and the element base", () => {
    for (const n of ["signal", "computed", "effect", "batch", "untracked", "root", "onCleanup", "h", "fragment", "list", "show", "keyed", "cx", "LabcoatElement", "define"])
      expect(core, n).toHaveProperty(n);
  });

  it("./components and the root re-export the same bindings; the alias resolves to the source", () => {
    for (const n of ["icon", "statusIcon", "button", "iconButton", "chip", "badge", "statusBadge", "tooltip", "dialog", "toast", "animateEnter"])
      expect((components as Record<string, unknown>)[n], n).toBe((pkg as Record<string, unknown>)[n]);
    expect(pkg.THEME_FAMILIES).toBeDefined();
    expect(viaAlias.h).toBe(core.h);
    expect(viaAlias.icon).toBe(components.icon);
  });

  it("registers the lc-* element forms", () => {
    for (const tag of ["lc-icon", "lc-status-icon", "lc-button", "lc-chip", "lc-badge", "lc-kbd", "lc-message-bar", "lc-logo"]) expect(customElements.get(tag), tag).toBeDefined();
  });
});
