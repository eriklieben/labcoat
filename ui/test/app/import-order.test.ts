import { describe, expect, it, vi } from "vitest";

// Regression: settings.ts, ui-defaults.ts and store.ts used to import each other in a loop, so importing
// ui-defaults first crashed settings.ts ("Cannot read properties of undefined (reading 'add')").
describe("module import order", () => {
  it("ui-defaults can be imported before the store", async () => {
    vi.resetModules();
    await expect(import("../../src/ui-defaults")).resolves.toBeDefined();
    await expect(import("../../src/settings")).resolves.toBeDefined();
    await expect(import("../../src/store")).resolves.toBeDefined();
  });
  it("settings can be imported first", async () => {
    vi.resetModules();
    await expect(import("../../src/settings")).resolves.toBeDefined();
  });
});
