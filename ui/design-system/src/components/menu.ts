/**
 * Menus. WAI-ARIA APG "Menu Button" / "Menu":
 *
 *   menuButton({ label: "More actions", icon: "settings", iconOnly: true, items: () => [
 *     { label: "Copy name", icon: "copy", shortcut: "Mod+C", onSelect: copy },
 *     { separator: true },
 *     { heading: "Sort" },
 *     { label: "By name", type: "radio", checked: sort() === "name", onSelect: () => setSort("name") },
 *   ] })
 *
 *   // or controlled, anchored to any element:
 *   menu({ open, anchor: () => btn, onClose: () => open.set(false), label: "Run actions", items })
 *
 * Markup: `div.lc-popover.lc-menu > div[role=menu] > button.lc-menu-item[role=menuitem|menuitemradio|menuitemcheckbox]`,
 * `div.lc-menu-sep[role=separator]`, `div.lc-menu-heading`. Rendered in document.body with fixed positioning.
 *
 * Keyboard: on the button Enter / Space / ArrowDown open at the first item (the checked one in radio-only menus), ArrowUp at the last; in the
 * menu ArrowUp/ArrowDown (wrapping), Home, End, a letter jumps to the next item starting with it, Enter / Space
 * activate, Escape closes and returns focus to the button, Tab closes.
 */
import { h, read, signal, type Child, type ClassValue, type MaybeReactive } from "../core";
import { button, type ButtonVariant } from "./button";
import { icon, type IconName } from "./icon";
import { kbd } from "./kbd";
import { popover, type CloseReason, type Placement } from "./popover";
import { rovingIndex, uid } from "./util";

export interface MenuItem {
  label: Child;
  /** plain text of the label for type-ahead (defaults to the label when it is a string) */
  text?: string;
  icon?: IconName;
  description?: Child;
  shortcut?: string;
  type?: "item" | "radio" | "checkbox";
  checked?: boolean;
  disabled?: boolean;
  danger?: boolean;
  /** keep the menu open after selecting (checkbox items) */
  keepOpen?: boolean;
  onSelect: () => void;
}
export type MenuEntry = MenuItem | { separator: true } | { heading: string };

const isItem = (e: MenuEntry): e is MenuItem => "onSelect" in e;

export interface MenuOptions {
  open: MaybeReactive<boolean>;
  anchor: () => HTMLElement | null | undefined;
  onClose: (reason: CloseReason) => void;
  items: MaybeReactive<readonly MenuEntry[]>;
  /** accessible name of the menu */
  label: string;
  placement?: Placement;
  class?: ClassValue;
  /** "last" focuses the last item on open (ArrowUp on the button) */
  focus?: () => "first" | "last";
  id?: string;
  /** min width = anchor width */
  matchWidth?: boolean;
}

const ITEM_ROLE = { item: "menuitem", radio: "menuitemradio", checkbox: "menuitemcheckbox" } as const;

/** The check column of a radio or checkbox item. */
const checkMark = (checked: boolean) => h("span", { class: "lc-menu-check", "aria-hidden": "true" }, checked ? icon("checkmark", { size: 16 }) : null);

/** Type-ahead: the next enabled item after `cur` (wrapping) whose text starts with the typed character. */
function typeAheadIndex(e: KeyboardEvent, items: readonly HTMLButtonElement[], cur: number): number | undefined {
  if (e.key.length !== 1 || !/\S/.test(e.key) || e.ctrlKey || e.metaKey || e.altKey) return undefined;
  const ch = e.key.toLowerCase();
  for (let n = 1; n <= items.length; n++) {
    const j = (cur + n) % items.length;
    if (!items[j].disabled && (items[j].dataset.text ?? "").toLowerCase().startsWith(ch)) return j;
  }
  return undefined;
}

export function menu(opts: MenuOptions): Child {
  return popover({
    open: opts.open,
    anchor: opts.anchor,
    onClose: opts.onClose,
    placement: opts.placement ?? "bottom-start",
    role: null,
    class: ["lc-menu", opts.class],
    matchWidth: opts.matchWidth,
    initialFocus: (el) => {
      const items = [...el.querySelectorAll<HTMLButtonElement>(".lc-menu-item:not(:disabled)")];
      if (opts.focus?.() === "last") return items.at(-1);
      // a pure choice menu (only radio items, e.g. a run picker) opens on the checked item; action menus on the first
      const choiceOnly = items.length > 0 && items.every((b) => b.getAttribute("role") === "menuitemradio");
      return (choiceOnly ? items.find((b) => b.getAttribute("aria-checked") === "true") : undefined) ?? items[0];
    },
    content: () => {
      const list = h("div", { class: "lc-menu-list", role: "menu", id: opts.id, "aria-label": opts.label, onkeydown }, () => read(opts.items).map(renderEntry));
      function itemsNow() {
        return [...list.querySelectorAll<HTMLButtonElement>(".lc-menu-item")];
      }
      function onkeydown(e: KeyboardEvent) {
        const items = itemsNow();
        const cur = items.indexOf(document.activeElement as HTMLButtonElement);
        const i = rovingIndex(e, items.length, cur, "vertical", (j) => items[j].disabled) ?? typeAheadIndex(e, items, cur);
        if (e.key === "Tab") opts.onClose("tab");
        if (i === undefined) return;
        e.preventDefault();
        items[i].focus();
      }
      function renderEntry(e: MenuEntry): Child {
        if ("separator" in e) return h("div", { class: "lc-menu-sep", role: "separator" });
        if ("heading" in e) return h("div", { class: "lc-menu-heading", role: "presentation" }, e.heading);
        if (!isItem(e)) return null;
        const type = e.type ?? (e.checked !== undefined ? "radio" : "item");
        const text = e.text ?? (typeof e.label === "string" ? e.label : "");
        return h(
          "button",
          {
            type: "button",
            class: ["lc-menu-item", e.danger && "danger"],
            role: ITEM_ROLE[type],
            "aria-checked": type === "item" ? undefined : !!e.checked,
            tabIndex: -1,
            disabled: !!e.disabled,
            "data-text": text,
            onclick: () => {
              e.onSelect();
              if (!e.keepOpen) opts.onClose("select");
            },
          },
          type === "item" ? null : checkMark(!!e.checked),
          e.icon ? icon(e.icon, { size: 16, class: "lc-menu-icon" }) : null,
          h("span", { class: "lc-menu-text" }, h("span", { class: "lc-menu-label" }, e.label), e.description ? h("span", { class: "lc-menu-desc" }, e.description) : null),
          e.shortcut ? kbd(e.shortcut, { class: "lc-menu-kbd" }) : null,
        );
      }
      return list;
    },
  });
}

export interface MenuButtonOptions {
  /** button text (or the accessible name when iconOnly) */
  label: string;
  items: MaybeReactive<readonly MenuEntry[]>;
  icon?: IconName;
  iconOnly?: boolean;
  variant?: ButtonVariant;
  size?: "default" | "small";
  placement?: Placement;
  disabled?: MaybeReactive<boolean>;
  /** menu accessible name (default: label) */
  menuLabel?: string;
  /** show a chevron after the label (default true unless iconOnly) */
  chevron?: boolean;
  class?: ClassValue;
  menuClass?: ClassValue;
}

/** A button that opens a menu (aria-haspopup, aria-expanded, aria-controls wired). */
export function menuButton(opts: MenuButtonOptions): HTMLButtonElement {
  const open = signal(false);
  const focusAt = signal<"first" | "last">("first");
  const id = uid("menu");
  let btn!: HTMLButtonElement;
  const openAt = (where: "first" | "last") => {
    focusAt.set(where);
    open.set(true);
  };
  const common = {
    "aria-haspopup": "menu",
    "aria-expanded": () => open(),
    "aria-controls": () => (open() ? id : undefined),
    disabled: opts.disabled,
    ref: (b: HTMLButtonElement) => (btn = b),
    onclick: () => (open.peek() ? open.set(false) : openAt("first")),
    onkeydown: (e: KeyboardEvent) => {
      if (e.key === "ArrowDown" || e.key === "ArrowUp") {
        e.preventDefault();
        openAt(e.key === "ArrowUp" ? "last" : "first");
      }
    },
  };
  const region = menu({
    open,
    anchor: () => btn,
    onClose: () => open.set(false),
    items: opts.items,
    label: opts.menuLabel ?? opts.label,
    placement: opts.placement ?? "bottom-start",
    focus: () => focusAt.peek(),
    id,
    class: opts.menuClass,
  });
  if (opts.iconOnly && opts.icon) {
    return h(
      "button",
      { ...common, type: "button", class: ["icon-btn", opts.size === "small" && "small", opts.class], title: opts.label, "aria-label": opts.label },
      icon(opts.icon, { size: opts.size === "small" ? 16 : 18 }),
      region,
    );
  }
  return button(
    { ...common, type: "button", variant: opts.variant, size: opts.size, icon: opts.icon, class: opts.class },
    opts.label,
    opts.chevron === false ? null : icon("chevronDown", { size: 12, class: "lc-menu-chevron" }),
    region,
  );
}
