// The theme family ids for the screenshot / axe scripts, read from design-system/src/themes/families.ts (the one list
// the app and the gallery use). LC_FAMILIES=github,one limits a run to some families.
import { readFileSync } from "node:fs";

const src = readFileSync(new URL("../design-system/src/themes/families.ts", import.meta.url), "utf8");
const all = [...src.matchAll(/^\s+id: "([a-z0-9-]+)",$/gm)].map((m) => m[1]);
const only = process.env.LC_FAMILIES?.split(",").map((s) => s.trim()).filter(Boolean);

/** every family id (Settings order), or the LC_FAMILIES subset */
export const FAMILIES = only ? all.filter((f) => only.includes(f)) : all;
/** "<family>-light" / "<family>-dark" for each family (the gallery's ?theme= values) */
export const THEME_KEYS = FAMILIES.flatMap((f) => [`${f}-light`, `${f}-dark`]);
