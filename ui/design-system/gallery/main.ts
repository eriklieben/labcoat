// Labcoat design system gallery: theme switcher, foundations (brand, tokens, type, spacing, motion) and every primitive.
import "../src/styles.css";
import "./gallery.css";
import faviconUrl from "../../public/favicon.svg?url";
import { badge, brandLogo, effect, h, icon, isThemeFamily, root, segmented, signal, THEME_FAMILIES, type ThemeFamily } from "../src";
import { componentSections } from "./components";
import { brandSection, colorSection, motionSection, spacingSection, typographySection, type ThemeKey } from "./foundations";
import { installPseudoStates, sections } from "./util";

type Mode = "light" | "dark";

const html = document.documentElement;
const family = signal<ThemeFamily>(isThemeFamily(html.dataset.themeFamily) ? html.dataset.themeFamily : "aspire");
const mode = signal<Mode>((html.dataset.theme as Mode) || "light");
const current = () => `${family()}-${mode()}` as ThemeKey;

const link = document.createElement("link");
link.rel = "icon";
link.type = "image/svg+xml";
link.href = faviconUrl;
document.head.append(link);

installPseudoStates();

root(() => {
  effect(() => {
    html.dataset.themeFamily = family();
    html.dataset.theme = mode();
    try {
      localStorage.setItem("lc-gallery-theme", current());
    } catch {
      /* storage blocked: the theme just isn't remembered */
    }
  });

  const activeId = signal("intro");

  const header = h(
    "header",
    { class: "g-header" },
    h(
      "a",
      { class: "g-brand", href: "#intro" },
      brandLogo({ size: 28 }),
      h("span", { class: "g-brand-name" }, "Labcoat"),
      h("span", { class: "g-brand-sub" }, "Design System"),
    ),
    h("span", { class: "g-header-spacer" }),
    h(
      "div",
      { class: "g-theme" },
      h("label", { class: "g-theme-label", htmlFor: "g-theme-family" }, "Theme"),
      // a native select: seven families don't fit a segmented control in the header
      h(
        "select",
        {
          id: "g-theme-family",
          class: "g-select",
          value: family,
          onchange: (e: Event) => family.set((e.currentTarget as HTMLSelectElement).value as ThemeFamily),
        },
        THEME_FAMILIES.map((f) => h("option", { value: f.id, selected: () => family() === f.id }, `${f.label} · ${f.flavors.light} / ${f.flavors.dark}`)),
      ),
      segmented({
        label: "Mode",
        size: "small",
        value: mode,
        onChange: mode.set,
        options: [
          { value: "light", label: "Light", icon: "sun" },
          { value: "dark", label: "Dark", icon: "moon" },
        ],
      }),
    ),
  );

  const content = [
    h(
      "div",
      { class: "g-hero", id: "intro" },
      h(
        "div",
        null,
        h("h1", null, "Labcoat Design System"),
        h(
          "p",
          null,
          "Plain TypeScript primitives for the Labcoat test explorer: a tiny reactive core (alien-signals + h()), semantic --lc-* tokens with seven theme families (Catppuccin, Aspire, GitHub, One, Dracula, Solarized, Gruvbox) in light and dark, and accessible factories and lc-* elements. Switch the theme in the header: every swatch and component below follows it.",
        ),
        h("div", { class: "g-hero-stats" }, badge({}, `${THEME_FAMILIES.length * 2} themes`), badge({}, "24 primitives"), badge({}, "WAI-ARIA APG patterns"), badge({}, "prefers-reduced-motion")),
      ),
      h("div", { class: "g-hero-art", "aria-hidden": "true" }, brandLogo({ size: 88 })),
    ),
    brandSection(),
    colorSection(current),
    typographySection(),
    spacingSection(),
    motionSection(),
    ...componentSections(),
  ];

  const groups = ["Overview", "Foundations", "Components"] as const;
  const nav = h(
    "nav",
    { class: "g-nav", "aria-label": "Gallery sections" },
    groups.map((g) => [
      h("h2", null, g),
      h(
        "ul",
        null,
        (g === "Overview" ? [{ id: "intro", title: "Introduction", icon: "info" as const, group: g }, ...sections.filter((s) => s.group === g)] : sections.filter((s) => s.group === g)).map((s) =>
          h(
            "li",
            null,
            h("a", { href: `#${s.id}`, "aria-current": () => (activeId() === s.id ? "true" : undefined) }, s.icon ? icon(s.icon, { size: 16 }) : null, s.title),
          ),
        ),
      ),
    ]),
  );

  const main = h("main", { class: "g-main", id: "main" }, h("div", { class: "g-content" }, content));
  document.getElementById("root")!.replaceWith(h("div", { class: "g-app" }, header, nav, main));

  const io = new IntersectionObserver(
    (entries) => {
      const vis = entries.filter((e) => e.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top);
      if (vis[0]) activeId.set(vis[0].target.id);
    },
    { root: main, rootMargin: "0px 0px -70% 0px" },
  );
  for (const el of main.querySelectorAll(".g-section, #intro")) io.observe(el);
}, null);
