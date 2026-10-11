using AspireSample.Api;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints(); // /health and /alive

app.MapGet("/", () => Results.Redirect("/orders"));

var orders = app.MapGroup("/orders");

orders.MapGet("/", (OrderStore store) => Results.Ok(store.GetAll()));

orders.MapGet("/{id:int}", (int id, OrderStore store) =>
    store.Get(id) is { } order ? Results.Ok(order) : Results.NotFound());

orders.MapPost("/", (CreateOrderRequest request, OrderStore store, ILogger<Program> logger) =>
{
    var errors = OrderPricing.Validate(request);
    if (errors.Count > 0)
    {
        // Shows up in the dashboard's structured logs, correlated with the request's trace (and the test that sent it).
        logger.LogWarning("Rejected order for {Customer}: {Errors}", request.Customer,
            string.Join("; ", errors.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m}"))));
        return Results.ValidationProblem(errors);
    }

    var order = store.Add(request.Customer!.Trim(), request.Items!);
    logger.LogInformation("Created order {OrderId} for {Customer} ({ItemCount} items, total {Total})", order.Id, order.Customer, order.Items.Count, order.Total);
    return Results.Created($"/orders/{order.Id}", order);
});

app.Run();

public partial class Program;
