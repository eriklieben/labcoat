import { describe, expect, it } from "vitest";
import { validate } from "../src/orders";

describe("validate", () => {
  it("accepts a valid order", () => {
    expect(validate({ customer: "Contoso", items: [{ sku: "A", quantity: 1, unitPrice: 1 }] })).toEqual({});
  });

  it("requires a customer", () => {
    expect(validate({ customer: " ", items: [{ sku: "A", quantity: 1, unitPrice: 1 }] })).toHaveProperty("customer");
  });

  it("requires items", () => {
    expect(validate({ customer: "Contoso" }).items).toEqual(["An order must contain at least one item."]);
  });

  describe("items", () => {
    it("reports every problem of an item", () => {
      expect(validate({ customer: "Contoso", items: [{ sku: "", quantity: 0, unitPrice: -1 }] }).items).toEqual([
        "Item 0: SKU is required.",
        "Item 0: quantity must be greater than zero.",
        "Item 0: unit price cannot be negative.",
      ]);
    });

    it.skip("checks the SKU against the catalog (needs the catalog API)", () => {});
  });
});
