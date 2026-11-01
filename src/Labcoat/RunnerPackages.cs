using System.Security;

namespace Labcoat;

/// <summary>
/// Runner packages for the standalone tool: a dotnet tool can't reference other packages, so <c>--runners vitest</c>
/// without an installed runner fetches <c>Labcoat.Vitest</c> of Labcoat's own version into the NuGet global packages
/// folder (a <c>PackageDownload</c> restore of a scratch project, so nuget.config sources and credentials apply) and uses
/// the runner from there. Labcoat.Aspire.Hosting doesn't need this: the AppHost's package reference brings the runner.
/// </summary>
public static class RunnerPackages
{
    /// <summary>"vitest" → "Labcoat.Vitest".</summary>
    public static string PackageIdOf(string runnerId) =>
        "Labcoat." + string.Concat(runnerId.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    /// <summary>The NuGet global packages folder: $NUGET_PACKAGES, else ~/.nuget/packages.</summary>
    public static string GlobalPackagesFolder() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    /// <summary>The runner folder of a package in the global packages folder (it may not exist).</summary>
    public static string RunnerDirectory(string packagesFolder, string runnerId, string version) =>
        Path.Combine(packagesFolder, PackageIdOf(runnerId).ToLowerInvariant(), version.ToLowerInvariant(), "runner");

    /// <summary>
    /// The runner <paramref name="runnerId"/> of Labcoat's version: from the global packages folder, else restored into
    /// it first. Null (and a warning) when the package can't be restored.
    /// </summary>
    public static async Task<RunnerPlugin?> FetchAsync(string runnerId, string version, string workingDirectory, Action<string> info, Action<string> warn,
        CancellationToken ct)
    {
        var packages = GlobalPackagesFolder();
        var dir = RunnerDirectory(packages, runnerId, version);
        var manifest = Path.Combine(dir, RunnerPlugin.ManifestFileName);
        if (!File.Exists(manifest))
        {
            var packageId = PackageIdOf(runnerId);
            info($"Fetching {packageId} {version} (the '{runnerId}' test runner)...");
            if (!await RestoreAsync(packageId, version, workingDirectory, warn, ct) || !File.Exists(manifest)) return null;
        }
        return RunnerPlugin.Load(manifest, warn);
    }

    private static async Task<bool> RestoreAsync(string packageId, string version, string workingDirectory, Action<string> warn, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "labcoat", "runner-restore", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(scratch);
            var project = Path.Combine(scratch, "restore.csproj");
            await File.WriteAllTextAsync(project, ScratchProject(packageId, version), ct);
            // The sources (and credentials) of the workspace's nuget.config, not of the temp folder.
            string[] config = NuGetConfigOf(workingDirectory) is { } file ? ["--configfile", file] : [];
            var (code, output) = await DotnetCli.RunAsync(["restore", project, .. config], scratch, ct);
            if (code == 0) return true;
            warn($"Could not fetch {packageId} {version}:\n{DotnetCli.TailLines(output, 8)}");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            warn($"Could not fetch {packageId} {version}: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover in the temp folder */ }
        }
    }

    /// <summary>A project that only downloads the package (no assets, no build); outside the workspace, so no Directory.*.props apply.</summary>
    internal static string ScratchProject(string packageId, string version) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
            <PackageDownload Include="{SecurityElement.Escape(packageId)}" Version="[{SecurityElement.Escape(version)}]" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>The nearest nuget.config at or above <paramref name="dir"/>.</summary>
    private static string? NuGetConfigOf(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            try
            {
                var config = d.EnumerateFiles().FirstOrDefault(f => f.Name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase));
                if (config is not null) return config.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable folder: restore with the default sources */ return null; }
        }
        return null;
    }
}
