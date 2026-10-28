using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Labcoat;

/// <summary>The ProjectReference graph of a test project: which projects (and directories) its tests depend on.</summary>
internal static class ProjectReferences
{
    /// <summary>
    /// Project files of the transitive ProjectReference closure (MSBuild gives us the direct references; deeper levels are
    /// read from the project files without evaluation, so conditional/property-based references are skipped).
    /// <paramref name="assembliesOnly"/>: only references whose assembly ends up in the test app (not
    /// ReferenceOutputAssembly="false", not an Aspire project resource).
    /// </summary>
    public static List<string> Closure(JsonNode? directRefs, bool assembliesOnly = false)
    {
        var cmp = PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var seen = new HashSet<string>(cmp);
        var queue = new Queue<string>(DirectReferences(directRefs, assembliesOnly));
        while (queue.TryDequeue(out var proj))
        {
            if (!File.Exists(proj) || !seen.Add(proj)) continue; // a missing project is no source root
            foreach (var r in Of(proj, assembliesOnly)) queue.Enqueue(r);
        }
        return [.. seen];
    }

    /// <summary>The project files of MSBuild's evaluated ProjectReference items.</summary>
    private static List<string> DirectReferences(JsonNode? directRefs, bool assembliesOnly)
    {
        var result = new List<string>();
        if (directRefs is not JsonArray arr) return result;
        foreach (var r in arr)
        {
            if (r?["FullPath"]?.GetValue<string>() is not { } full) continue;
            if (assembliesOnly && NotReferencedAssembly(r["ReferenceOutputAssembly"]?.GetValue<string>(), r["IsAspireProjectResource"]?.GetValue<string>())) continue;
            result.Add(full);
        }
        return result;
    }

    /// <summary>The ProjectReferences read from a project file (no evaluation: conditional/property-based ones are skipped).</summary>
    public static List<string> Of(string proj, bool assembliesOnly)
    {
        var refs = new List<string>();
        try
        {
            var dir = Path.GetDirectoryName(proj)!;
            var doc = XDocument.Load(proj);
            // An Aspire AppHost's ProjectReferences are resources (separate processes), unless marked otherwise.
            var appHost = IsAspireAppHost(doc);
            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = (string?)el.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include) || include.Contains("$(")) continue;
                if (assembliesOnly && IsNotReferencedAssembly(el, appHost)) continue;
                refs.AddRange(include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(part => Path.GetFullPath(Path.Combine(dir, part.Replace('\\', Path.DirectorySeparatorChar)))));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { /* unreadable project file: no references from it */ }
        return refs;
    }

    private static bool IsNotReferencedAssembly(XElement projectReference, bool inAppHost) =>
        NotReferencedAssembly(Metadata(projectReference, "ReferenceOutputAssembly"),
            Metadata(projectReference, "IsAspireProjectResource") ?? (inAppHost ? "true" : null));

    private static bool NotReferencedAssembly(string? referenceOutputAssembly, string? aspireResource) =>
        string.Equals(referenceOutputAssembly?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
        || string.Equals(aspireResource?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsAspireAppHost(XDocument doc) =>
        ((string?)doc.Root?.Attribute("Sdk"))?.Contains("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase) == true
        || doc.Descendants().Any(e => (e.Name.LocalName == "Sdk" && ((string?)e.Attribute("Name"))?.Equals("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase) == true)
                                      || (e.Name.LocalName == "IsAspireHost" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)));

    private static string? Metadata(XElement el, string name) =>
        (string?)el.Attribute(name) ?? el.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    /// <summary>Directories of <see cref="Closure"/>.</summary>
    public static List<string> ClosureDirs(JsonNode? directRefs)
    {
        var cmp = PathUtil.Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        return Closure(directRefs).Select(p => Path.GetDirectoryName(p)!).Distinct(cmp).ToList();
    }
}
