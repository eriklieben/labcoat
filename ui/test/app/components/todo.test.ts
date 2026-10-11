import { describe, expect, it } from "vitest";
import { todo, type Todo } from "../../../src/components/todo";
import { mount, text } from "../../helpers/app";

describe("<lc-todo>", () => {
  it("renders a placeholder for an unported component with inline styles", () => {
    const el = mount(todo("DetailPane")) as Todo;
    expect(text(el)).toBe("TODO (phase 2): DetailPane");
    expect(el.style.display).toBe("flex");
    expect(el.style.flex).toBe("1 1 auto");
  });

  it("inline variant keeps its own flex; the name attribute updates the text", () => {
    const el = mount(todo("LogPanel", true)) as Todo;
    expect(el.style.flex).toBe("0 0 auto");
    el.setAttribute("name", "Other");
    expect(text(el)).toBe("TODO (phase 2): Other");
    el.removeAttribute("name");
    expect(text(el)).toBe("TODO (phase 2): ?");
  });
});
