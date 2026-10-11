import { beforeEach, describe, expect, it, vi } from "vitest";
import "../../../src/components/settings-page";
import { resetLocalSettings, settings } from "../../../src/settings";
import { setUiDefaults } from "../../../src/ui-defaults";
import { themeFamily, themePref, THEME_FAMILIES } from "../../../src/theme";
import { flushFrame, key, makeStore, mount, stateEvent, text } from "../../helpers/app";
import { jsonResponse } from "../../helpers/app3";
import { clipboard, flushMicrotasks } from "../../helpers/dom";

const group = (page: Element, label: string) => page.querySelector<HTMLElement>(`[role=radiogroup][aria-label="${label}"]`)!;
const radio = (page: Element, groupLabel: string, optionText: string) =>
  [...group(page, groupLabel).querySelectorAll<HTMLButtonElement>("[role=radio]")].find((b) => text(b).startsWith(optionText))!;
const sw = (page: Element, label: string) => page.querySelector<HTMLInputElement>(`input[role=switch][aria-label="${label}"]`)!;
const rowOf = (el: Element) => el.closest(".setting-row")!;

function setup(state = {}) {
  const ctx = makeStore({ kind: "ws", state });
  const page = mount(document.createElement("lc-settings-page"));
  return { ...ctx, page };
}

beforeEach(() => {
  setUiDefaults({});
  resetLocalSettings();
});

describe("<lc-settings-page>", () => {
  it("is a labelled region with the sections", () => {
    const { page } = setup();
    expect(page.getAttribute("role")).toBe("region");
    expect(page.getAttribute("aria-label")).toBe("Settings");
    expect(page.classList.contains("settings-page")).toBe(true);
    const sections = [...page.querySelectorAll(".settings-section-title")].map((e) => e.textContent);
    expect(sections).toEqual(["Appearance", "Keyboard", "Editor", "Coverage", "Celebrations", "History", "Output", "About"]);
    expect(text(page.querySelector(".settings-note"))).toBe("Stored in this browser only, except where noted.");
  });

  it("theme, mode and motion segmented controls set the theme / settings", () => {
    const { page } = setup();
    expect(radio(page, "Theme", "Catppuccin").getAttribute("aria-checked")).toBe("true");
    radio(page, "Theme", "Aspire").click();
    expect(themeFamily()).toBe("aspire");
    expect(radio(page, "Theme", "Aspire").classList.contains("on")).toBe(true);
    radio(page, "Mode", "Dark").click();
    expect(themePref()).toBe("dark");
    radio(page, "Motion", "Reduced").click();
    expect(settings().motion).toBe("reduced");
    expect(document.documentElement.dataset.motion).toBe("reduced");
    // origin hint: set in this browser
    expect(text(rowOf(group(page, "Motion")).querySelector(".setting-origin"))).toBe("Set in this browser");
  });

  it("Reset to defaults forgets this browser's choices", () => {
    const { page } = setup();
    const reset = page.querySelector<HTMLButtonElement>(".settings-reset")!;
    expect(reset.disabled).toBe(true);
    radio(page, "Coverage tint", "Gutter only").click();
    expect(settings().coverageTint).toBe("gutter");
    expect(reset.disabled).toBe(false);
    reset.click();
    expect(settings().coverageTint).toBe("line");
    expect(reset.disabled).toBe(true);
  });

  it("every switch writes its boolean setting", () => {
    const { page } = setup();
    const cases: [string, keyof ReturnType<typeof settings>][] = [
      ["Single-key shortcuts", "singleKeyShortcuts"],
      ["Confetti when tests turn green", "confetti"],
      ["Show partial runs in history", "partialRunsInHistory"],
      ["Open the output panel on errors", "openOutputOnError"],
    ];
    for (const [label, k] of cases) {
      const input = sw(page, label);
      const before = settings()[k];
      expect(input.checked).toBe(before);
      input.checked = !before;
      input.dispatchEvent(new Event("change", { bubbles: true }));
      expect(settings()[k]).toBe(!before);
      expect(input.checked).toBe(!before);
    }
  });

  it("editor choice: options follow the host's editor list", async () => {
    const { page, store } = setup({ editors: [{ id: "vscode", name: "Visual Studio Code", available: true }, { id: "visualstudio", name: "Visual Studio", available: false }] });
    await flushFrame();
    const vs = radio(page, "Editor", "Visual Studio");
    // "Visual Studio Code" also starts with "Visual Studio": pick the exact one
    const exact = [...group(page, "Editor").querySelectorAll<HTMLButtonElement>("[role=radio]")].find((b) => text(b) === "Visual Studio")!;
    expect(vs).toBeTruthy();
    expect(exact.disabled).toBe(true);
    expect(exact.title).toContain("was not found");
    const hint = text(rowOf(group(page, "Editor")).querySelector(".setting-desc"));
    expect(hint).toContain("Not found: Visual Studio (Windows only)");
    radio(page, "Editor", "JetBrains Rider").click();
    expect(settings().editor).toBe("rider");
    // all available again: the hint goes back to the base text
    store.apply(stateEvent({ editors: [{ id: "visualstudio", name: "Visual Studio", available: true }] }));
    await flushFrame();
    expect(text(rowOf(group(page, "Editor")).querySelector(".setting-desc"))).not.toContain("Not found");
  });

  it("Aspire tracing switch only with an OTLP endpoint; optimistic until the next snapshot", async () => {
    const { page, store, transport } = setup({ tracingAvailable: false });
    await flushFrame();
    expect(sw(page, "Traces in the Aspire dashboard")).toBeNull();
    store.apply(stateEvent({ tracingAvailable: true, tracingEnabled: false }));
    await flushFrame();
    const input = sw(page, "Traces in the Aspire dashboard");
    expect(input.checked).toBe(false);
    input.checked = true;
    input.dispatchEvent(new Event("change"));
    expect(transport.sent).toContainEqual({ type: "setTracing", enabled: true });
    expect(input.checked).toBe(true);
    // the host confirms
    store.apply(stateEvent({ tracingAvailable: true, tracingEnabled: true }));
    await flushFrame();
    expect(sw(page, "Traces in the Aspire dashboard").checked).toBe(true);
    expect(page.querySelector(".setting-trace-filter")).toBeNull();
  });

  it("shows the host's trace filter under the tracing switch (read-only)", async () => {
    const { page, store } = setup({ tracingAvailable: true, tracingEnabled: true, tracingFilter: "cat:Integration" });
    await flushFrame();
    const hint = page.querySelector(".setting-trace-filter")!;
    expect(text(hint)).toBe("Only tests matching cat:Integration are traced (set by the Labcoat host).");
    expect(hint.querySelector("code")!.textContent).toBe("cat:Integration");
    expect(rowOf(sw(page, "Traces in the Aspire dashboard")).contains(hint)).toBe(true);
    // the host restarts without a filter: the hint goes away
    store.apply(stateEvent({ tracingAvailable: true, tracingEnabled: true, tracingFilter: undefined }));
    await flushFrame();
    expect(page.querySelector(".setting-trace-filter")).toBeNull();
  });

  it("More themes: radio cards with roving focus", () => {
    const { page } = setup();
    const btn = page.querySelector<HTMLButtonElement>(".more-themes .lc-disclosure-btn")!;
    expect(btn.getAttribute("aria-expanded")).toBe("false");
    btn.click();
    expect(btn.getAttribute("aria-expanded")).toBe("true");
    const grid = page.querySelector<HTMLElement>(".theme-grid[role=radiogroup]")!;
    const cards = [...grid.querySelectorAll<HTMLButtonElement>("[role=radio]")];
    const more = THEME_FAMILIES.filter((f) => !f.primary);
    expect(cards).toHaveLength(more.length);
    expect(cards[0].tabIndex).toBe(0);
    expect(cards[1].tabIndex).toBe(-1);
    cards[1].click();
    expect(themeFamily()).toBe(more[1].id);
    expect(cards[1].getAttribute("aria-checked")).toBe("true");
    expect(cards[1].tabIndex).toBe(0);
    expect(text(page.querySelector(".more-themes-current"))).toBe(`${more[1].label} selected`);
    // arrows move and select (wrapping), Home/End too
    key(cards[1], "ArrowRight");
    expect(themeFamily()).toBe(more[2].id);
    key(cards[2], "Home");
    expect(themeFamily()).toBe(more[0].id);
    key(cards[0], "ArrowLeft");
    expect(themeFamily()).toBe(more[more.length - 1].id);
    // other keys are ignored
    key(cards[0], "x");
    expect(themeFamily()).toBe(more[more.length - 1].id);
    // a main family: nothing in "More themes" is checked
    radio(page, "Theme", "Aspire").click();
    expect(cards.every((c) => c.getAttribute("aria-checked") === "false")).toBe(true);
    expect(page.querySelector(".more-themes-current")).toBeNull();
  });

  it("opens More themes when one of them is active", () => {
    const more = THEME_FAMILIES.filter((f) => !f.primary);
    localStorage.setItem("mste.themeFamily", more[0].id);
    // theme overrides are read at module load; set through the radio instead
    const { page } = setup();
    page.querySelector<HTMLButtonElement>(".more-themes .lc-disclosure-btn")!.click();
    page.querySelector<HTMLButtonElement>(".theme-grid [role=radio]")!.click();
    page.remove();
    const again = mount(document.createElement("lc-settings-page"));
    expect(again.querySelector(".more-themes .lc-disclosure-btn")!.getAttribute("aria-expanded")).toBe("true");
  });

  describe("host defaults and locks (uiDefaults)", () => {
    it("shows where a default comes from", () => {
      setUiDefaults({ coverageTint: "gutter", sources: { coverageTint: "workspace" }, files: { workspace: ".labcoat.json" } });
      const { page } = setup();
      expect(text(page.querySelector(".settings-note"))).toContain("Defaults come from the Labcoat host where shown.");
      expect(settings().coverageTint).toBe("gutter");
      const row = rowOf(group(page, "Coverage tint"));
      expect(text(row.querySelector(".setting-origin"))).toBe("Default from .labcoat.json");
      radio(page, "Coverage tint", "Full line").click();
      expect(text(row.querySelector(".setting-origin"))).toBe("Set in this browser · default from .labcoat.json");
    });

    it("a locked setting is disabled and says who locked it", () => {
      setUiDefaults({ confetti: true, theme: "dracula", locked: ["confetti", "theme", "motion"], lockedBy: { confetti: "apphost", theme: "env", motion: "cli" } });
      const { page } = setup();
      const confetti = sw(page, "Confetti when tests turn green");
      expect(confetti.disabled).toBe(true);
      expect(confetti.checked).toBe(true);
      const row = rowOf(confetti);
      expect(row.classList.contains("setting-locked")).toBe(true);
      expect(text(row.querySelector(".setting-origin-locked"))).toBe("Set by the AppHost (Program.cs)");
      // the theme segmented control and the More themes cards are disabled
      expect(group(page, "Theme").getAttribute("aria-disabled")).toBe("true");
      expect(radio(page, "Theme", "Aspire").disabled).toBe(true);
      expect(themeFamily()).toBe("dracula");
      expect(text(rowOf(group(page, "Theme")).querySelector(".setting-origin-locked"))).toBe("Set by LABCOAT_UI_SETTINGS");
      expect(text(rowOf(group(page, "Motion")).querySelector(".setting-origin-locked"))).toBe("Set by the command line");
      // More themes: open (dracula is one of them), cards disabled, keys ignored
      const grid = page.querySelector<HTMLElement>(".theme-grid")!;
      expect(grid.getAttribute("aria-disabled")).toBe("true");
      const card = grid.querySelector<HTMLButtonElement>("[role=radio]")!;
      expect(card.disabled).toBe(true);
      key(card, "ArrowRight");
      expect(themeFamily()).toBe("dracula");
    });
  });
});

describe("<lc-settings-page> about", () => {
  it("copies the diagnostics report", async () => {
    const { page } = setup();
    const fetch = vi.fn(async () => jsonResponse({ markdown: "### Labcoat diagnostics" }));
    vi.stubGlobal("fetch", fetch);
    const button = [...page.querySelectorAll<HTMLButtonElement>("button")].find((b) => text(b) === "Copy diagnostics")!;
    expect(text(rowOf(button).querySelector(".setting-label"))).toBe("Diagnostics");
    button.click();
    await flushMicrotasks();
    expect(String((fetch.mock.calls[0] as unknown[])[0])).toContain("/api/diagnostics");
    expect(clipboard.text).toBe("### Labcoat diagnostics");
    vi.unstubAllGlobals();
  });
});
