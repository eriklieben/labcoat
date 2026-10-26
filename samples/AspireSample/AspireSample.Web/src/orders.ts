// The orders front end's pricing and validation: the same rules as AspireSample.Api's OrderPricing, so the form can show
// a total and errors before the order is sent.

export interface OrderItem {
  sku: string;
  quantity: number;
  unitPrice: number;
}

export interface CreateOrderRequest {
  customer?: string;
  items?: OrderItem[];
}

/** The order total, rounded to cents. */
export function calculateTotal(items: OrderItem[]): number {
  const cents = items.reduce((sum, i) => sum + i.quantity * Math.round(i.unitPrice * 100), 0);
  return cents / 100;
}

/** Validation errors by field; empty when the request is valid. */
export function validate(request: CreateOrderRequest): Record<string, string[]> {
  const errors: Record<string, string[]> = {};
  if (!request.customer?.trim()) errors.customer = ["Customer is required."];
  if (!request.items?.length) {
    errors.items = ["An order must contain at least one item."];
    return errors;
  }
  const itemErrors: string[] = [];
  request.items.forEach((item, i) => {
    if (!item.sku.trim()) itemErrors.push(`Item ${i}: SKU is required.`);
    if (item.quantity <= 0) itemErrors.push(`Item ${i}: quantity must be greater than zero.`);
    if (item.unitPrice < 0) itemErrors.push(`Item ${i}: unit price cannot be negative.`);
  });
  if (itemErrors.length > 0) errors.items = itemErrors;
  return errors;
}

/** The receipt lines the confirmation page shows. */
export function receiptLines(items: OrderItem[]): string[] {
  return [
    ...items.map((i) => `${i.quantity} x ${i.sku} @ ${i.unitPrice.toFixed(2)}`),
    `Total: ${calculateTotal(items).toFixed(2)}`,
  ];
}
