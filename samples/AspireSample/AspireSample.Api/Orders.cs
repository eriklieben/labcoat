using System.Collections.Concurrent;

namespace AspireSample.Api;

public sealed record OrderItem(string Sku, int Quantity, decimal UnitPrice);

public sealed record CreateOrderRequest(string? Customer, List<OrderItem>? Items);

public sealed record Order(int Id, string Customer, IReadOnlyList<OrderItem> Items, DateTimeOffset CreatedAt)
{
    public decimal Total => OrderPricing.CalculateTotal(Items);
}

/// <summary>Pure pricing/validation logic, unit-testable without the Aspire app.</summary>
public static class OrderPricing
{
    public static decimal CalculateTotal(IEnumerable<OrderItem> items) =>
        items.Sum(i => i.Quantity * i.UnitPrice);

    /// <summary>Returns validation errors keyed by field; empty when the request is valid.</summary>
    public static Dictionary<string, string[]> Validate(CreateOrderRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Customer))
        {
            errors["customer"] = ["Customer is required."];
        }

        if (request.Items is null || request.Items.Count == 0)
        {
            errors["items"] = ["An order must contain at least one item."];
        }
        else
        {
            var itemErrors = new List<string>();
            for (var i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                if (string.IsNullOrWhiteSpace(item.Sku))
                    itemErrors.Add($"Item {i}: SKU is required.");
                if (item.Quantity <= 0)
                    itemErrors.Add($"Item {i}: quantity must be greater than zero.");
                if (item.UnitPrice < 0)
                    itemErrors.Add($"Item {i}: unit price cannot be negative.");
            }

            if (itemErrors.Count > 0)
                errors["items"] = itemErrors.ToArray();
        }

        return errors;
    }
}

/// <summary>Thread-safe in-memory order store (no database needed to run the sample).</summary>
public sealed class OrderStore
{
    private readonly ConcurrentDictionary<int, Order> _orders = new();
    private int _nextId;

    public OrderStore()
    {
        // Seed data so GET /orders is never empty.
        Add("Contoso", [new OrderItem("WIDGET-1", 2, 9.99m)]);
        Add("Fabrikam", [new OrderItem("GADGET-7", 1, 24.50m), new OrderItem("BOLT-3", 10, 0.25m)]);
    }

    public IEnumerable<Order> GetAll() => _orders.Values.OrderBy(o => o.Id);

    public Order? Get(int id) => _orders.TryGetValue(id, out var order) ? order : null;

    public Order Add(string customer, IReadOnlyList<OrderItem> items)
    {
        var id = Interlocked.Increment(ref _nextId);
        var order = new Order(id, customer, items, DateTimeOffset.UtcNow);
        _orders[id] = order;
        return order;
    }
}
