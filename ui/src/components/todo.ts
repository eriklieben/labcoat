/**
 * <lc-todo name="DetailPane">: placeholder for a component that has not been ported to web components yet
 * (phase 2). Inline styles on purpose: it must not need anything from the app CSS.
 */
import { define, h, LabcoatElement } from "@eriklieben/labcoat-design-system";

export interface TodoProps {
  name: string;
}
export interface Todo extends TodoProps {}
export class Todo extends LabcoatElement<TodoProps> {
  static defaults: TodoProps = { name: "?" };
  static observedAttributes = ["name"];
  attributeChangedCallback(_a: string, _o: string | null, v: string | null) {
    this.name = v ?? "?";
  }
  protected render() {
    const extra = this.style.cssText; // e.g. the inline variant below sets flex: 0 0 auto
    this.style.cssText =
      "display:flex;align-items:center;justify-content:center;min-width:0;min-height:24px;flex:1 1 auto;margin:4px;padding:4px 8px;" +
      "border:1px dashed var(--lc-stroke);border-radius:6px;color:var(--lc-fg-muted);font-size:12px;" +
      extra;
    return h("span", () => `TODO (phase 2): ${this.props.name()}`);
  }
}
define("lc-todo", Todo);

/** `todo("LogPanel")` */
export const todo = (name: string, inline = false) => h("lc-todo", { name, style: inline ? "flex: 0 0 auto" : undefined });

declare global {
  interface HTMLElementTagNameMap {
    "lc-todo": Todo;
  }
}
