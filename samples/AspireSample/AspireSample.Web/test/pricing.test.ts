import { describe, expect, it } from "vitest";
import { calculateTotal, receiptLines } from "../src/orders";

describe("calculateTotal", () => {
  it.each([
    [1, 10.0, 10.0],
    [3, 2.5, 7.5],
    [10, 0.25, 2.5],
  ])("%i x %f = %f", (quantity, unitPrice, expected) => {
    expect(calculateTotal([{ sku: "SKU", quantity, unitPrice }])).toBe(expected);
  });

  it("sums all items", () => {
    expect(calculateTotal([{ sku: "A", quantity: 2, unitPrice: 9.99 }, { sku: "B", quantity: 1, unitPrice: 24.5 }])).toBe(44.48);
  });

  it("is zero without items", () => {
    expect(calculateTotal([])).toBe(0);
  });
});

describe("receiptLines", () => {
  it("lists every item and the total", () => {
    console.log("rendering the receipt of 2 items");
    expect(receiptLines([{ sku: "A", quantity: 2, unitPrice: 9.99 }, { sku: "B", quantity: 1, unitPrice: 24.5 }])).toEqual([
      "2 x A @ 9.99",
      "1 x B @ 24.50",
      "Total: 44.48",
    ]);
  });

  // Fails on purpose (like AspireSample.Tests): shows the expected / actual diff in Labcoat.
  it("shows the currency", () => {
    expect(receiptLines([{ sku: "A", quantity: 1, unitPrice: 5 }])).toEqual(["1 x A @ 5.00", "Total: EUR 5.00"]);
  });
});
