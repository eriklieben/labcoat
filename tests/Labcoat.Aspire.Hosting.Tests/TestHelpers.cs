using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Labcoat.Aspire.Hosting.Tests;

/// <summary>
/// A stand-in for the Labcoat REST API: canned responses per path (a queue per path; the last one repeats) and a log of
/// the requests the integration sent.
/// </summary>
internal sealed class FakeLabcoat : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _responses = new(StringComparer.Ordinal);

    public List<(HttpMethod Method, string PathAndQuery, string? Body, string? Token)> Requests { get; } = [];

    /// <summary>Queues a response for "METHOD /path?query".</summary>
    public FakeLabcoat On(string methodAndPath, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        if (!_responses.TryGetValue(methodAndPath, out var q)) _responses[methodAndPath] = q = new();
        q.Enqueue((status, body));
        return this;
    }

    public JsonNode? BodyOf(string methodAndPath) =>
        Requests.Where(r => $"{r.Method} {r.PathAndQuery}" == methodAndPath).Select(r => r.Body is null ? null : JsonNode.Parse(r.Body)).LastOrDefault();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var token = request.Headers.TryGetValues("X-Labcoat-Token", out var v) ? v.Single() : null;
        var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
        lock (Requests) Requests.Add((request.Method, request.RequestUri.PathAndQuery, body, token));
        if (!_responses.TryGetValue(key, out var q) || q.Count == 0)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
        var (status, text) = q.Count > 1 ? q.Dequeue() : q.Peek();
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

internal static class Hosting
{
    public static IDistributedApplicationBuilder Builder(string? projectDirectory = null, params string[] args) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = args,
            ProjectDirectory = projectDirectory,
        });

    /// <summary>A Labcoat resource with an allocated endpoint whose HTTP calls go to <paramref name="fake"/>.</summary>
    public static IResourceBuilder<LabcoatResource> WithFake(this IResourceBuilder<LabcoatResource> rb, FakeLabcoat fake)
    {
        var endpoint = rb.Resource.Annotations.OfType<EndpointAnnotation>().Single();
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 4541);
        rb.Resource.Http = new HttpClient(fake);
        rb.Resource.StatusPollInterval = TimeSpan.FromMilliseconds(1);
        rb.Resource.SuitePollInterval = TimeSpan.FromMilliseconds(10);
        return rb;
    }

    public static async Task<List<string>> ArgsAsync(LabcoatResource resource)
    {
        var args = new List<object>();
        var ctx = new CommandLineArgsCallbackContext(args, resource, CancellationToken.None);
        foreach (var a in resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>()) await a.Callback(ctx);
        return args.Select(a => a.ToString()!).ToList();
    }

    public static async Task<Dictionary<string, object>> EnvironmentAsync(LabcoatResource resource)
    {
        var env = new Dictionary<string, object>();
        var ctx = new EnvironmentCallbackContext(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource, env);
        foreach (var a in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) await a.Callback(ctx);
        return env;
    }

    public static ResourceCommandAnnotation Command(LabcoatResource resource, string name) =>
        resource.Annotations.OfType<ResourceCommandAnnotation>().Single(c => c.Name == name);

    public static Task<ExecuteCommandResult> ExecuteAsync(LabcoatResource resource, string name) =>
        Command(resource, name).ExecuteCommand(new ExecuteCommandContext
        {
            // ServiceProvider: the 13.4 name (the minimum this package supports; 13.6 calls it Services).
            ServiceProvider = new ServiceCollection().BuildServiceProvider(),
#pragma warning disable ASPIREINTERACTION001 // experimental in 13.4
            Arguments = new InteractionInputCollection([]),
#pragma warning restore ASPIREINTERACTION001
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            ResourceName = resource.Name,
            CancellationToken = CancellationToken.None,
        });
}
