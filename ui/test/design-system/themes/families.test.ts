import { readFileSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import {
  DEFAULT_THEME_FAMILY,
  flavorNames,
  isThemeFamily,
  THEME_FAMILIES,
  THEME_FAMILY_IDS,
  themeFamilyInfo,
  type ThemeFamily,
} from "../../../design-system/src/themes/families";

const themesDir = resolve(import.meta.dirname, "../../../design-system/src/themes") + "/";
const stylesCss = readFileSync(resolve(import.meta.dirname, "../../../design-system/src/styles.css"), "utf8");
const HEX = /^#[0-9a-f]{6}$/;

describe("THEME_FAMILIES", () => {
  it("has unique ids, the default first, and complete entries", () => {
    expect(new Set(THEME_FAMILY_IDS).size).toBe(THEME_FAMILIES.length);
    expect(THEME_FAMILY_IDS[0]).toBe(DEFAULT_THEME_FAMILY);
    for (const f of THEME_FAMILIES) {
      expect(f.id, f.id).toMatch(/^[a-z]+$/);
      expect(f.label.length).toBeGreaterThan(0);
      expect(f.description.length).toBeGreaterThan(0);
      expect(f.modes).toEqual(["light", "dark"]);
      for (const m of f.modes) {
        expect(f.flavors[m].length, `${f.id} ${m} flavor`).toBeGreaterThan(0);
        expect(f.swatches[m]).toHaveLength(6);
        for (const c of f.swatches[m]) expect(c, `${f.id} ${m}`).toMatch(HEX);
      }
      // background and text differ per mode
      expect(f.swatches.light[0]).not.toBe(f.swatches.dark[0]);
    }
    expect(THEME_FAMILIES.filter((f) => f.primary).map((f) => f.id)).toEqual(["catppuccin", "aspire"]);
  });

  it("every family has a CSS file imported by styles.css that assigns its light and dark tokens", () => {
    const files = readdirSync(themesDir).filter((f) => f.endsWith(".css"));
    expect(files.sort()).toEqual(THEME_FAMILY_IDS.map((id) => `${id}.css`).sort());
    for (const id of THEME_FAMILY_IDS) {
      expect(stylesCss, id).toContain(`@import "./themes/${id}.css" layer(lc.themes);`);
      const css = readFileSync(themesDir + `${id}.css`, "utf8");
      if (id === "aspire") {
        // the fallback family: plain :root (light) and [data-theme="dark"]
        expect(css).toMatch(/^:root \{/m);
        expect(css).toMatch(/^:root\[data-theme="dark"\] \{/m);
      } else {
        expect(css, id).toContain(`:root[data-theme-family="${id}"] {`);
        expect(css, id).toContain(`:root[data-theme-family="${id}"][data-theme="dark"] {`);
      }
      expect(css).toMatch(/--lc-[a-z-]+:/);
    }
  });

  it("isThemeFamily / themeFamilyInfo / flavorNames", () => {
    expect(isThemeFamily("dracula")).toBe(true);
    expect(isThemeFamily("nope")).toBe(false);
    expect(isThemeFamily(3)).toBe(false);
    expect(themeFamilyInfo("github").label).toBe("GitHub");
    expect(themeFamilyInfo("missing" as ThemeFamily).id).toBe("catppuccin");
    expect(flavorNames(themeFamilyInfo("catppuccin"))).toBe("Latte / Macchiato");
  });
});
