import { describe, expect, it } from "vitest";
import { root, signal } from "../../../design-system/src/core";
import { propertyGrid, type PropertyRow } from "../../../design-system/src/components/property-grid";

describe("propertyGrid()", () => {
  it("renders a description list of name/value pairs", () => {
    const dl = propertyGrid(
      [
        { name: "Status", value: "Passed" },
        { name: "Tags", value: ["a", "b"], badges: true },
        { name: "Id", value: "x.y", mono: true },
      ],
      { label: "Test properties", class: "x" },
    );
    expect(dl.tagName).toBe("DL");
    expect(dl.className).toBe("prop-grid x");
    expect(dl.getAttribute("aria-label")).toBe("Test properties");
    expect([...dl.querySelectorAll("dt.prop-name")].map((d) => d.textContent)).toEqual(["Status", "Tags", "Id"]);
    const dds = dl.querySelectorAll("dd");
    expect(dds[0].className).toBe("prop-value");
    expect(dds[1].className).toBe("prop-value badges");
    expect(dds[1].textContent).toBe("ab");
    expect(dds[2].className).toBe("prop-value mono");
    expect(propertyGrid([]).hasAttribute("aria-label")).toBe(false);
  });

  it("reactive rows: rebuilt when the names change, values stay reactive", () => {
    root((dispose) => {
      const dur = signal("1 ms");
      const rows = signal<PropertyRow[]>([{ name: "Duration", value: () => dur() }]);
      const dl = propertyGrid(rows);
      expect(dl.querySelector("dd")!.textContent).toBe("1 ms");
      dur.set("2 ms");
      expect(dl.querySelector("dd")!.textContent).toBe("2 ms");
      const dt = dl.querySelector("dt");
      rows.set([{ name: "Duration", value: () => dur() }]);
      expect(dl.querySelector("dt")).toBe(dt); // same names: not rebuilt
      rows.set([{ name: "Duration", value: "x" }, { name: "Last run", value: "now" }]);
      expect([...dl.querySelectorAll("dt")].map((d) => d.textContent)).toEqual(["Duration", "Last run"]);
      dispose();
    });
  });
});
