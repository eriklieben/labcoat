using Microsoft.Extensions.FileProviders;

namespace Labcoat;

/// <summary>The web UI of the host: security middleware, index.html, static files and the location of wwwroot.</summary>
internal static class WebUi
{
    /// <summary>The security middleware (Host / Origin / token checks, response headers), index.html and the static files.</summary>
    public static void Use(WebApplication app, RequestGuard guard, SecurityHeaders securityHeaders, IndexPage indexPage, string webRoot)
    {
        app.Use(async (ctx, next) =>
        {
            securityHeaders.Apply(ctx);
            if (guard.Check(ctx) is { } rejection)
            {
                ctx.Response.StatusCode = rejection.Status;
                await ctx.Response.WriteAsJsonAsync(new { error = rejection.Message });
                return;
            }
            await next(ctx);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Use(async (ctx, next) =>
        {
            if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
                && ctx.Request.Path.Value is "/" or "/index.html")
            {
                await ServeIndex(ctx, indexPage);
                return;
            }
            await next(ctx);
        });
        var fileProvider = new PhysicalFileProvider(webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            // index.html must be revalidated (it names the hashed bundles of the installed version); hashed assets never change.
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
                ctx.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
        });
    }

    public static async Task ServeIndex(HttpContext ctx, IndexPage page)
    {
        var html = page.Html();
        if (html is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync("UI not built yet - run npm run build in ui/", ctx.RequestAborted);
            return;
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        await ctx.Response.WriteAsync(html, ctx.RequestAborted);
    }

    /// <summary>
    /// wwwroot next to the binaries (installed tool / published), or, for <c>dotnet run</c> from source (wwwroot is not
    /// copied to bin), the project directory's wwwroot found walking up from <paramref name="baseDirectory"/>.
    /// </summary>
    public static string FindWebRoot(string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDirectory);
        for (var d = dir; d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Labcoat.csproj")) && Directory.Exists(Path.Combine(d.FullName, "wwwroot")))
                return Path.Combine(d.FullName, "wwwroot");
        }
        var local = Path.Combine(baseDirectory, "wwwroot");
        Directory.CreateDirectory(local);
        return local;
    }
}
