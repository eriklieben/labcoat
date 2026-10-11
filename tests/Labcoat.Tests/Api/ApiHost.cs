using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Labcoat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Labcoat.Tests.Api;

/// <summary>
/// The real Labcoat web app (<see cref="LabcoatApp.Build"/>) on an in-memory <see cref="TestServer"/>, over a temp
/// workspace: one (fake) test project <c>A.Tests</c>, a web root with an index.html that has an inline script, and an
/// Explorer whose tests are seeded directly (no build, no test host).
/// </summary>
internal sealed class ApiHost : IAsyncDisposable
{
    public const string InlineScript = "document.documentElement.dataset.boot = \"1\";";
    public const string IndexHtml =
        $"<!doctype html>\n<html lang=\"en\"><head><script>{InlineScript}</script><script type=\"module\" src=\"/assets/app-1234.js\"></script></head><body></body></html>";

    public required DirectoryInfo Dir { get; init; }
    public required string Workspace { get; init; }
    public required string WebRoot { get; init; }
    public string? ProjectPath { get; init; }
    public required WebApplication App { get; init; }
    public required TestServer Server { get; init; }
    public required HttpClient Client { get; init; }
    public Explorer Explorer => App.Services.GetRequiredService<Explorer>();
    public EventHub Hub => App.Services.GetRequiredService<EventHub>();
    public const string ProjectId = "A.Tests";

    /// <param name="withProject">A project A.Tests (a project file without targets: building it fails fast).</param>
    /// <param name="withUi">Write wwwroot/index.html (+ an asset and a favicon); false = "UI not built".</param>
    /// <param name="beforeStart">Runs with the project path before the app starts (e.g. to write its history).</param>
    public static async Task<ApiHost> StartAsync(bool withProject = true, bool withUi = true, JsonObject? uiDefaults = null,
        RequestGuard? guard = null, Action<string>? beforeStart = null)
    {
        var dir = Directory.CreateTempSubdirectory("labcoat-api-");
        var workspace = Directory.CreateDirectory(Path.Combine(dir.FullName, "ws")).FullName;
        var webRoot = Directory.CreateDirectory(Path.Combine(dir.FullName, "wwwroot")).FullName;
        if (withUi)
        {
            File.WriteAllText(Path.Combine(webRoot, "index.html"), IndexHtml);
            Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
            File.WriteAllText(Path.Combine(webRoot, "assets", "app-1234.js"), "console.log('app');");
            File.WriteAllText(Path.Combine(webRoot, "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        }
        string? project = null;
        if (withProject)
        {
            var projectDir = Directory.CreateDirectory(Path.Combine(workspace, ProjectId)).FullName;
            project = Path.Combine(projectDir, ProjectId + ".csproj");
            File.WriteAllText(project, "<Project />");
            beforeStart?.Invoke(project);
        }

        var app = LabcoatApp.Build(new LabcoatAppOptions
        {
            Paths = [workspace],
            Projects = project is null ? [] : [project],
            Guard = guard ?? new RequestGuard([]),
            WebRoot = webRoot,
            UiDefaults = uiDefaults,
            WorkspaceName = "api-tests",
        }, b => b.WebHost.UseTestServer());
        await app.StartAsync();
        var server = app.GetTestServer();
        return new ApiHost
        {
            Dir = dir, Workspace = workspace, WebRoot = webRoot, ProjectPath = project, App = app, Server = server,
            Client = server.CreateClient(),
        };
    }

    /// <summary>A test of A.Tests located in a file under the workspace.</summary>
    public TestNode Test(string id, Outcome outcome = Outcome.NotRun, string? stdOut = null, string? traceId = null) => new()
    {
        Id = id,
        ProjectId = ProjectId,
        DisplayName = "Test_" + id,
        Namespace = "Shop",
        ClassName = "CartTests",
        MethodName = "Test_" + id,
        FilePath = Path.Combine(Workspace, ProjectId, "CartTests.cs"),
        Line = 10,
        Outcome = outcome,
        StdOut = stdOut,
        TraceId = traceId,
    };

    public void Seed(params TestNode[] tests) => Explorer.SeedTests(tests);

    public Task<HttpResponseMessage> PostJson(string path, string json) =>
        Client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    public Task<HttpResponseMessage> PostRaw(string path, string body, string? contentType)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = contentType is null ? null : MediaTypeHeaderValue.Parse(contentType);
        return Client.PostAsync(path, content);
    }

    public async Task<JsonNode> GetJson(string path)
    {
        using var res = await Client.GetAsync(path);
        var text = await res.Content.ReadAsStringAsync();
        Assert.IsTrue(res.IsSuccessStatusCode, $"GET {path}: {(int)res.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    public static async Task<JsonNode?> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        try { Dir.Delete(recursive: true); } catch { }
    }
}
