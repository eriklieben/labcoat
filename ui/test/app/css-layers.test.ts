import { readFileSync, readdirSync } from "node:fs";
import { relative, resolve } from "node:path";
import { describe, expect, it } from "vitest";

// Unlayered CSS beats every layer, so one forgotten wrapper silently overrides the whole app (see ARCHITECTURE.md).
const uiDir = resolve(import.meta.dirname, "../..");
const appSrc = resolve(uiDir, "src");
const dsSrc = resolve(uiDir, "design-system/src");
const LAYERS = ["lc.reset", "lc.tokens", "lc.themes", "lc.components", "lc.motion", "app.base", "app.shell", "app.components", "app.utilities"];

function cssFiles(dir: string): string[] {
  return readdirSync(dir, { recursive: true, encoding: "utf8" })
    .filter((f) => f.endsWith(".css"))
    .map((f) => resolve(dir, f));
}

/** The CSS with comments removed, so a leading license or doc comment doesn't count as content. */
const code = (file: string) => readFileSync(file, "utf8").replace(/\/\*[\s\S]*?\*\//g, "").trim();

describe("CSS cascade layers", () => {
  it("wraps every app stylesheet in one app.* layer", () => {
    const files = cssFiles(appSrc);
    expect(files.length).toBeGreaterThan(0);
    for (const file of files) {
      const css = code(file);
      const name = relative(uiDir, file);
      expect(css, name).toMatch(/^@layer app\.(base|shell|components|utilities) \{/);
      expect(css.endsWith("}"), name).toBe(true);
    }
  });

  it("imports every design-system stylesheet into a lc.* layer", () => {
    const styles = readFileSync(resolve(dsSrc, "styles.css"), "utf8");
    const imported = new Map([...styles.matchAll(/@import "\.\/([^"]+)" layer\(([\w.]+)\);/g)].map((m) => [m[1], m[2]]));
    for (const file of cssFiles(dsSrc)) {
      const path = relative(dsSrc, file);
      if (path === "styles.css") continue;
      expect(imported.get(path), `${path} is not imported with layer(…) in styles.css`).toMatch(/^lc\./);
    }
    expect(styles).not.toMatch(/@import "[^"]+";/);
  });

  it("declares the same layer order in styles.css and both index.html files", () => {
    const order = `@layer ${LAYERS.join(", ")};`;
    expect(readFileSync(resolve(dsSrc, "styles.css"), "utf8")).toContain(order);
    for (const html of ["index.html", "design-system/gallery/index.html"]) {
      expect(readFileSync(resolve(uiDir, html), "utf8"), html).toContain(order);
    }
  });
});
