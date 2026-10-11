/**
 * settings.ts / theme.ts / ui-defaults.ts: effective value = locked ? (host default ?? built-in)
 * : (this browser ?? host default ?? built-in). Modules read localStorage / <html data-labcoat-defaults> on import,
 * so every test loads them fresh.
 */
import { beforeEach, describe, expect, it, vi } from "vitest";
import { setMedia } from "../helpers/dom";
import { flushMicrotasks } from "../helpers/dom";

async function load() {
  vi.resetModules();
  // store first, like main.ts: store → settings → theme → ui-defaults → (store) is an import cycle, and importing
  // ui-defaults.ts first throws (settings.ts runs before ui-defaults.ts has initialized uiDefaultsListeners).
  const store = await import("../../src/store");
  const ui = await import("../../src/ui-defaults");
  const settings = await import("../../src/settings");
  const theme = await import("../../src/theme");
  const core = await import("@eriklieben/labcoat-design-system/core");
  return { ui, settings, theme, store, core };
}

beforeEach(() => {
  history.replaceState(null, "", "/");
});

describe("ui-defaults", () => {
  it("sanitizeUiDefaults keeps only known keys with valid values", async () => {
    const { ui } = await load();
    expect(ui.sanitizeUiDefaults(null)).toEqual({});
    expect(ui.sanitizeUiDefaults("x")).toEqual({});
    expect(
      ui.sanitizeUiDefaults({
        theme: "dracula",
        mode: "neon",
        motion: "off",
        editor: "rider",
        coverageTint: "gutter",
        confetti: true,
        openOutputOnError: "yes",
        partialRunsInHistory: false,
        singleKeyShortcuts: false,
        bogus: 1,
        locked: ["theme", "nope", "confetti"],
        sources: { theme: "apphost", motion: "moon", confetti: "cli" },
        lockedBy: "nope",
        files: { user: "~/.labcoat.json", workspace: 3 },
      }),
    ).toEqual({
      theme: "dracula",
      motion: "off",
      editor: "rider",
      coverageTint: "gutter",
      confetti: true,
      partialRunsInHistory: false,
      singleKeyShortcuts: false,
      locked: ["theme", "confetti"],
      sources: { theme: "apphost", confetti: "cli" },
      files: { user: "~/.labcoat.json", workspace: undefined },
    });
    expect(ui.UI_SETTING_KEYS).toContain("singleKeyShortcuts");
  });

  it("reads the host-injected <html data-labcoat-defaults> on load (bad JSON → none)", async () => {
    document.documentElement.setAttribute("data-labcoat-defaults", JSON.stringify({ theme: "github", locked: ["mode"] }));
    let { ui } = await load();
    expect(ui.uiDefaults.peek()).toEqual({ theme: "github", locked: ["mode"] });
    expect(ui.isLocked("mode")).toBe(true);
    expect(ui.isLocked("theme")).toBe(false);
    expect(ui.defaultOf("theme")).toBe("github");
    document.documentElement.setAttribute("data-labcoat-defaults", "{oops");
    ({ ui } = await load());
    expect(ui.uiDefaults.peek()).toEqual({});
  });

  it("mock hook: ?mock&uiDefaults=<json> acts like the injected attribute", async () => {
    history.replaceState(null, "", "/?mock&uiDefaults=" + encodeURIComponent(JSON.stringify({ confetti: true })));
    const { ui, store } = await load();
    expect(ui.uiDefaults.peek()).toEqual({ confetti: true });
    expect(document.documentElement.getAttribute("data-labcoat-defaults")).toBe('{"confetti":true}');
    // in mock mode a snapshot without uiDefaults keeps the URL hook's defaults
    ui.watchUiDefaults();
    const s = new store.Store("mock");
    s.attach({ kind: "mock", start() {}, send() {}, stop() {}, fetchSource: async () => "", fetchTestCoverage: async () => null, fetchRun: async () => null });
    s.apply({ type: "state", state: { projects: [], tests: [], watch: false, coverageEnabled: false } });
    expect(ui.uiDefaults.peek()).toEqual({ confetti: true });
  });

  it("setUiDefaults notifies listeners only on a real change", async () => {
    const { ui } = await load();
    const l = vi.fn();
    ui.onUiDefaultsChange(l);
    ui.setUiDefaults({ theme: "one" });
    ui.setUiDefaults({ theme: "one" });
    expect(l).toHaveBeenCalledTimes(1);
    expect(ui.uiDefaults.peek()).toEqual({ theme: "one" });
  });

  it("sourceLabel names every layer", async () => {
    const { ui } = await load();
    const d = { files: { user: "/home/me/.labcoat.json", workspace: "repo/.labcoat.json" } };
    expect(ui.sourceLabel("user", d)).toBe("/home/me/.labcoat.json");
    expect(ui.sourceLabel("user", {})).toBe("your user settings file");
    expect(ui.sourceLabel("workspace", d)).toBe("repo/.labcoat.json");
    expect(ui.sourceLabel("workspace", {})).toBe(".labcoat.json");
    expect(ui.sourceLabel("apphost")).toBe("the AppHost (Program.cs)");
    expect(ui.sourceLabel("env")).toBe("LABCOAT_UI_SETTINGS");
    expect(ui.sourceLabel("cli")).toBe("the command line");
    expect(ui.sourceLabel(undefined)).toBe("the Labcoat host");
  });

  it("watchUiDefaults follows state snapshots (once registered; none → cleared)", async () => {
    const { ui, store } = await load();
    const s = new store.Store("ws");
    s.attach({ kind: "ws", start() {}, send() {}, stop() {}, fetchSource: async () => "", fetchTestCoverage: async () => null, fetchRun: async () => null });
    ui.watchUiDefaults();
    ui.watchUiDefaults();
    const base = { projects: [], tests: [], watch: false, coverageEnabled: false };
    s.apply({ type: "state", state: { ...base, uiDefaults: { theme: "gruvbox", locked: ["theme"] } } });
    expect(ui.uiDefaults.peek()).toEqual({ theme: "gruvbox", locked: ["theme"] });
    s.apply({ type: "runsUpdated", runs: [] });
    expect(ui.uiDefaults.peek().theme).toBe("gruvbox");
    s.apply({ type: "state", state: base });
    expect(ui.uiDefaults.peek()).toEqual({});
  });
});

describe("settings", () => {
  it("built-in defaults with nothing stored or configured", async () => {
    const { settings } = await load();
    expect(settings.getSettings()).toEqual(settings.BUILTIN_SETTINGS);
    expect(settings.EDITORS.map((e) => e.id)).toEqual(["vscode", "rider", "visualstudio"]);
    expect(settings.hasLocalSettings()).toBe(false);
  });

  it("precedence: this browser > host default > built-in; a lock forces the host value (or built-in)", async () => {
    const { settings, ui } = await load();
    ui.setUiDefaults({ editor: "rider", confetti: true });
    expect(settings.getSettings()).toMatchObject({ editor: "rider", confetti: true });
    settings.setSetting("editor", "visualstudio");
    expect(settings.getSettings().editor).toBe("visualstudio");
    ui.setUiDefaults({ editor: "rider", confetti: true, locked: ["editor", "coverageTint"] });
    expect(settings.getSettings().editor).toBe("rider");
    expect(settings.getSettings().coverageTint).toBe("line"); // locked without a value → built-in
    settings.setSetting("editor", "vscode"); // ignored while locked
    settings.setSetting("coverageTint", "gutter");
    expect(settings.localSettings.peek()).toEqual({ editor: "visualstudio" });
    expect(settings.isSettingLocked("editor")).toBe(true);
    ui.setUiDefaults({});
    expect(settings.getSettings().editor).toBe("visualstudio");
  });

  it("stores only changed settings (v2 format) and loads them back", async () => {
    let { settings } = await load();
    settings.setSetting("confetti", true);
    expect(JSON.parse(localStorage.getItem("mste.settings")!)).toEqual({ confetti: true, $v: 2 });
    ({ settings } = await load());
    expect(settings.localSettings.peek()).toEqual({ confetti: true });
    expect(settings.getSettings().confetti).toBe(true);
    expect(settings.hasLocalSettings()).toBe(true);
  });

  it("migrates v1 (every setting stored): drops values equal to the built-in default and wrong types", async () => {
    localStorage.setItem("mste.settings", JSON.stringify({ confetti: false, editor: "rider", motion: 3, singleKeyShortcuts: true, coverageTint: "gutter" }));
    const { settings } = await load();
    expect(settings.localSettings.peek()).toEqual({ editor: "rider", coverageTint: "gutter" });
    expect(JSON.parse(localStorage.getItem("mste.settings")!)).toEqual({ editor: "rider", coverageTint: "gutter", $v: 2 });
  });

  it("v2 keeps a value equal to the built-in default (the user chose it)", async () => {
    localStorage.setItem("mste.settings", JSON.stringify({ confetti: false, $v: 2 }));
    const { settings, ui } = await load();
    ui.setUiDefaults({ confetti: true });
    expect(settings.getSettings().confetti).toBe(false);
  });

  it("applies the motion setting to <html data-motion> (also when the host default changes)", async () => {
    const { settings, ui } = await load();
    expect(document.documentElement.dataset.motion).toBeUndefined();
    settings.setSetting("motion", "reduced");
    expect(document.documentElement.dataset.motion).toBe("reduced");
    settings.resetLocalSettings();
    expect(document.documentElement.dataset.motion).toBeUndefined();
    ui.setUiDefaults({ motion: "off" });
    expect(document.documentElement.dataset.motion).toBe("off");
  });

  it("settingOrigin and lockedHint describe where a value comes from", async () => {
    const { settings, ui, theme } = await load();
    ui.setUiDefaults({ confetti: true, sources: { confetti: "workspace", editor: "user" }, locked: ["motion"], lockedBy: { motion: "apphost" } });
    expect(settings.settingOrigin("confetti")).toEqual({ locked: false, lockedBy: undefined, local: false, source: "workspace" });
    expect(settings.settingOrigin("editor").source).toBeUndefined(); // a source without a value is ignored
    expect(settings.settingOrigin("motion")).toMatchObject({ locked: true, lockedBy: "apphost" });
    expect(settings.lockedHint("motion")).toBe("Set by the AppHost (Program.cs)");
    expect(settings.lockedHint("confetti")).toBeUndefined();
    settings.setSetting("confetti", false);
    expect(settings.settingOrigin("confetti").local).toBe(true);
    expect(settings.settingOrigin("theme").local).toBe(false);
    theme.setThemeFamily("dracula");
    theme.setThemePref("dark");
    expect(settings.settingOrigin("theme").local).toBe(true);
    expect(settings.settingOrigin("mode").local).toBe(true);
  });

  it("resetLocalSettings forgets settings, theme and mode", async () => {
    const { settings, theme } = await load();
    settings.setSetting("editor", "rider");
    theme.setThemeFamily("one");
    theme.setThemePref("light");
    expect(settings.hasLocalSettings()).toBe(true);
    settings.resetLocalSettings();
    expect(settings.hasLocalSettings()).toBe(false);
    expect(localStorage.getItem("mste.settings")).toBeNull();
    expect(localStorage.getItem("mste.theme")).toBeNull();
    expect(localStorage.getItem("mste.themeFamily")).toBeNull();
    expect(settings.getSettings().editor).toBe("vscode");
  });

  it("survives a storage that throws", async () => {
    const { settings } = await load();
    const rm = vi.spyOn(Storage.prototype, "removeItem").mockImplementation(() => {
      throw new Error("denied");
    });
    expect(() => settings.resetLocalSettings()).not.toThrow();
    rm.mockRestore();
  });
});

describe("theme", () => {
  it("defaults: system mode, catppuccin family; reads valid stored overrides only", async () => {
    let { theme } = await load();
    expect(theme.themePref()).toBe("system");
    expect(theme.themeFamily()).toBe("catppuccin");
    localStorage.setItem("mste.theme", "dark");
    localStorage.setItem("mste.themeFamily", "bogus");
    ({ theme } = await load());
    expect(theme.themeOverrides.peek()).toEqual({ mode: "dark", family: undefined });
    expect(theme.themePref()).toBe("dark");
    expect(theme.THEME_FAMILIES.length).toBeGreaterThan(1);
  });

  it("precedence with host defaults and locks", async () => {
    const { theme, ui } = await load();
    ui.setUiDefaults({ theme: "dracula", mode: "dark" });
    expect([theme.themeFamily(), theme.themePref()]).toEqual(["dracula", "dark"]);
    theme.setThemeFamily("github");
    theme.setThemePref("light");
    expect([theme.themeFamily(), theme.themePref()]).toEqual(["github", "light"]);
    expect(localStorage.getItem("mste.themeFamily")).toBe("github");
    expect(localStorage.getItem("mste.theme")).toBe("light");
    ui.setUiDefaults({ theme: "dracula", mode: "dark", locked: ["theme", "mode"] });
    expect([theme.themeFamily(), theme.themePref()]).toEqual(["dracula", "dark"]);
    theme.setThemeFamily("one"); // ignored
    theme.setThemePref("system"); // ignored
    expect(theme.themeOverrides.peek()).toEqual({ family: "github", mode: "light" });
    ui.setUiDefaults({ locked: ["theme", "mode"] });
    expect([theme.themeFamily(), theme.themePref()]).toEqual(["catppuccin", "system"]);
  });

  it("followTheme applies at once on load, follows the OS while 'system', and stops on dispose", async () => {
    vi.useFakeTimers();
    const { theme, core } = await load();
    const html = document.documentElement;
    let dispose!: () => void;
    core.root((d) => {
      dispose = d;
      theme.followTheme();
    });
    expect([html.getAttribute("data-theme"), html.getAttribute("data-theme-family")]).toEqual(["light", "catppuccin"]);
    setMedia("(prefers-color-scheme: dark)", true);
    expect(html.getAttribute("data-theme")).toBe("dark");
    expect(html.classList.contains("theme-vt")).toBe(false); // not ready yet: instant
    theme.setThemePref("light");
    expect(html.getAttribute("data-theme")).toBe("light");
    setMedia("(prefers-color-scheme: dark)", false);
    setMedia("(prefers-color-scheme: dark)", true); // no longer followed
    expect(html.getAttribute("data-theme")).toBe("light");
    dispose();
    theme.setThemePref("dark");
    expect(html.getAttribute("data-theme")).toBe("light");
  });

  it("after the first painted frames, changes crossfade with a view transition", async () => {
    vi.useFakeTimers();
    const { theme, core } = await load();
    const html = document.documentElement;
    const svt = vi.spyOn(document as unknown as { startViewTransition: (cb: () => void) => unknown }, "startViewTransition");
    core.root(() => theme.followTheme());
    vi.advanceTimersToNextFrame();
    vi.advanceTimersToNextFrame();
    theme.setThemeFamily("dracula");
    expect(svt).toHaveBeenCalledTimes(1);
    expect(html.getAttribute("data-theme-family")).toBe("dracula");
    expect(html.classList.contains("theme-vt")).toBe(true);
    theme.setThemeFamily("one"); // skips the previous one
    await flushMicrotasks();
    expect(html.classList.contains("theme-vt")).toBe(false);
    theme.setThemeFamily("one"); // unchanged: nothing
    expect(svt).toHaveBeenCalledTimes(2);
  });

  it("without View Transitions: a short color transition class; reduced motion / hidden tab: instant", async () => {
    vi.useFakeTimers();
    delete (document as unknown as Record<string, unknown>).startViewTransition;
    const { theme, core } = await load();
    const html = document.documentElement;
    core.root(() => theme.followTheme());
    vi.advanceTimersToNextFrame();
    vi.advanceTimersToNextFrame();
    theme.setThemeFamily("github");
    expect(html.classList.contains("theme-transitioning")).toBe(true);
    vi.advanceTimersByTime(250);
    expect(html.classList.contains("theme-transitioning")).toBe(false);
    html.dataset.motion = "reduced";
    theme.setThemeFamily("one");
    expect(html.classList.contains("theme-transitioning")).toBe(false);
    expect(html.getAttribute("data-theme-family")).toBe("one");
    delete html.dataset.motion;
    vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");
    theme.setThemeFamily("aspire");
    expect(html.classList.contains("theme-transitioning")).toBe(false);
  });

  it("storage errors are ignored", async () => {
    const get = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("denied");
    });
    const { theme } = await load();
    get.mockRestore();
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("denied");
    });
    expect(() => theme.setThemePref("dark")).not.toThrow();
    expect(theme.themePref()).toBe("dark");
  });
});
