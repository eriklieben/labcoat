/**
 * Copy-to-clipboard icon button with a confirm morph (P8): the copy glyph shrinks out (80 ms), a green check grows in
 * (160 ms), holds 1.2 s, then crossfades back. The tooltip/title says "Copied" and a polite live region announces it.
 *
 *   copyButton({ text: () => fqName(test()), label: "Copy fully qualified name" })
 *
 * Markup: `button.icon-btn.small.lc-copy[.done] > span.icon.lc-copy-icon + span.icon.lc-copy-check`.
 */
import { getOwner, h, onCleanup, read, signal, type ClassValue, type MaybeReactive } from "../core";
import { icon } from "./icon";
import { announce } from "./util";

export interface CopyButtonOptions {
  /** text to copy (read on click) */
  text: MaybeReactive<string>;
  /** accessible name, e.g. "Copy error" */
  label: string;
  size?: number;
  class?: ClassValue;
  /** called after a successful copy */
  onCopied?: () => void;
}

async function writeClipboard(text: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // insecure context / permission: the old way
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.setAttribute("readonly", "");
    ta.style.cssText = "position:fixed;opacity:0;pointer-events:none";
    document.body.append(ta);
    ta.select();
    const ok = document.execCommand("copy");
    ta.remove();
    if (!ok) throw new Error("copy failed");
  }
}

export function copyButton(opts: CopyButtonOptions): HTMLButtonElement {
  const { size = 14 } = opts;
  const done = signal(false);
  let timer = 0;
  if (getOwner()) onCleanup(() => window.clearTimeout(timer));
  return h(
    "button",
    {
      type: "button",
      class: () => ["icon-btn", "small", "lc-copy", done() && "done", opts.class],
      title: () => (done() ? "Copied" : opts.label),
      "aria-label": opts.label,
      onclick: (e: MouseEvent) => {
        e.stopPropagation();
        writeClipboard(read(opts.text)).then(
          () => {
            done.set(true);
            announce("Copied");
            opts.onCopied?.();
            window.clearTimeout(timer);
            timer = window.setTimeout(() => done.set(false), 1200);
          },
          () => announce("Copy failed"),
        );
      },
    },
    icon("copy", { size, class: "lc-copy-icon" }),
    icon("checkmark", { size, class: "lc-copy-check" }),
  );
}
