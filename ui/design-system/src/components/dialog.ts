/**
 * Modal dialog. WAI-ARIA APG "Dialog (Modal)" on a native `<dialog>` opened with showModal(): the rest of the page
 * is inert, the dialog sits in the top layer (no z-index / overflow issues), Tab wraps inside, Escape and a backdrop
 * click call `onClose`, and focus returns to the element that had it when the dialog opened.
 *
 *   const open = signal(false);
 *   dialog({ open, onClose: () => open.set(false), title: "Keyboard shortcuts", size: "md",
 *     content: () => shortcutTable(), footer: () => button({ onclick: () => open.set(false) }, "Close") })
 *
 *   // command palette shell: no header, near the top, the input gets focus
 *   dialog({ open, onClose, label: "Command palette", placement: "top", bare: true, initialFocus: "input",
 *     content: () => palette() })
 *
 * Markup: `<dialog class="lc-dialog lc-dialog-<size> [lc-dialog-top] [bare]" aria-labelledby|aria-label>
 * <div class="lc-dialog-header"><h2 class="lc-dialog-title"/>close</div><div class="lc-dialog-body"/>
 * <footer class="lc-dialog-footer"/></dialog>`, rendered into document.body while open.
 */
import { h, onCleanup, read, show, type Child, type ClassValue, type MaybeReactive } from "../core";
import { iconButton } from "./button";
import { canAnimate } from "./motion";
import { focusables, portal, uid } from "./util";

export interface DialogOptions {
  open: MaybeReactive<boolean>;
  onClose: (reason: "escape" | "backdrop" | "close-button") => void;
  /** visible title (also the accessible name) */
  title?: Child;
  /** accessible name when there is no visible title */
  label?: string;
  /** a short description under the title (aria-describedby) */
  description?: Child;
  content: () => Child;
  footer?: () => Child;
  size?: "sm" | "md" | "lg";
  placement?: "center" | "top";
  /** no padding around the content (palettes, custom layouts) */
  bare?: boolean;
  /** show the × button in the header (default true when there is a title) */
  closeButton?: boolean;
  /** close on backdrop click (default true) */
  dismissOnBackdrop?: boolean;
  /** selector or function for the element to focus on open (default: the first focusable in the body) */
  initialFocus?: string | ((dialog: HTMLDialogElement) => HTMLElement | null | undefined);
  class?: ClassValue;
}

export function dialog(opts: DialogOptions): Child {
  const { size = "md", placement = "center", bare = false, dismissOnBackdrop = true } = opts;
  return show(
    () => read(opts.open),
    () => {
      const returnTo = document.activeElement as HTMLElement | null;
      const titleId = uid("dlg-title");
      const descId = uid("dlg-desc");
      const hasTitle = opts.title != null;
      let downOnBackdrop = false;
      const el = h(
        "dialog",
        {
          class: ["lc-dialog", `lc-dialog-${size}`, placement === "top" && "lc-dialog-top", bare && "bare", opts.class],
          "aria-labelledby": hasTitle ? titleId : undefined,
          "aria-label": hasTitle ? undefined : opts.label,
          "aria-describedby": opts.description != null ? descId : undefined,
          "aria-modal": "true",
          oncancel: (e: Event) => {
            e.preventDefault();
            opts.onClose("escape");
          },
          onclose: () => {
            // closed natively (e.g. a second Escape the browser would not let us cancel): sync the state
            if (read(opts.open)) opts.onClose("escape");
          },
          onpointerdown: (e: PointerEvent) => (downOnBackdrop = e.target === el),
          onclick: (e: MouseEvent) => {
            if (dismissOnBackdrop && downOnBackdrop && e.target === el) opts.onClose("backdrop");
            downOnBackdrop = false;
          },
          onkeydown: (e: KeyboardEvent) => trapTab(el, e),
        },
        h(
          "div",
          { class: "lc-dialog-surface" },
          dialogHeader(opts, hasTitle, titleId, descId),
          h("div", { class: "lc-dialog-body" }, opts.content()),
          opts.footer ? h("footer", { class: "lc-dialog-footer" }, opts.footer()) : null,
        ),
      );
      portal(el, undefined, { exit: exitDialog });
      el.showModal();
      document.documentElement.classList.add("lc-modal-open");
      // Registered after portal(): runs before removal.
      onCleanup(() => {
        document.documentElement.classList.remove("lc-modal-open");
        if (el.open) el.close();
        if (returnTo?.isConnected) returnTo.focus({ preventScroll: true });
      });
      (initialFocusTarget(el, opts.initialFocus) ?? el).focus();
      return null;
    },
  );
}

/**
 * The header (title, description, close button), or without a title and close button only the description, kept for
 * screen readers.
 */
function dialogHeader(opts: DialogOptions, hasTitle: boolean, titleId: string, descId: string): Child {
  if (!hasTitle && !opts.closeButton) {
    return opts.description != null ? h("p", { class: "lc-dialog-desc visually-hidden", id: descId }, opts.description) : null;
  }
  const showClose = opts.closeButton ?? hasTitle;
  return h(
    "div", // not <header>: inside a dialog it would be a second page "banner" landmark (axe landmark-no-duplicate-banner)
    { class: "lc-dialog-header" },
    h(
      "div",
      { class: "lc-dialog-heading" },
      hasTitle ? h("h2", { class: "lc-dialog-title", id: titleId }, opts.title) : null,
      opts.description != null ? h("p", { class: "lc-dialog-desc", id: descId }, opts.description) : null,
    ),
    showClose ? iconButton({ icon: "dismiss", label: "Close", class: "lc-dialog-close", onclick: () => opts.onClose("close-button") }) : null,
  );
}

/** Tab and Shift+Tab wrap inside the dialog. */
function trapTab(el: HTMLDialogElement, e: KeyboardEvent) {
  if (e.key !== "Tab") return;
  const f = focusables(el);
  if (!f.length) return e.preventDefault();
  const first = f[0];
  const last = f.at(-1)!;
  if (e.shiftKey && (document.activeElement === first || document.activeElement === el)) {
    e.preventDefault();
    last.focus();
  } else if (!e.shiftKey && document.activeElement === last) {
    e.preventDefault();
    first.focus();
  }
}

/** P9: after close() the CSS exit transition runs (the dialog stays in the top layer); remove it when it ends. */
function exitDialog(d: HTMLDialogElement, done: () => void) {
  if (!canAnimate("fade")) return done();
  d.inert = true;
  const anims = d.getAnimations({ subtree: true });
  if (!anims.length) return done();
  Promise.allSettled(anims.map((a) => a.finished)).then(done);
  window.setTimeout(done, 300);
}

/** The element to focus on open: `initialFocus` (a function or a selector), else the first focusable in the body. */
function initialFocusTarget(el: HTMLDialogElement, initialFocus: DialogOptions["initialFocus"]): HTMLElement | null | undefined {
  if (typeof initialFocus === "function") return initialFocus(el);
  if (initialFocus) return el.querySelector<HTMLElement>(initialFocus);
  const body = el.querySelector<HTMLElement>(".lc-dialog-body")!;
  return focusables(body)[0] ?? focusables(el)[0];
}
