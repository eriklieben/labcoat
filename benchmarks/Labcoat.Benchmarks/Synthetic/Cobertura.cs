using System.Globalization;
using System.Xml;

namespace Labcoat.Benchmarks.Synthetic;

/// <summary>
/// Writes Cobertura XML in the shape of Microsoft.Testing.Extensions.CodeCoverage's output (see
/// tests/Labcoat.Tests/Fixtures/Cobertura): one package per module, one class per product file, method-level and
/// class-level lines, a branch on every fourth line. Generated per benchmark run into a temp directory.
/// </summary>
public static class Cobertura
{
    /// <summary>
    /// A report of <paramref name="classes"/>' product files. <paramref name="hitFactor"/> varies the hit counts;
    /// with <paramref name="coverAll"/> every method is hit (a subset run that adds coverage), otherwise every other one.
    /// The test assembly is included as a package too (Labcoat drops it).
    /// </summary>
    public static void Write(string xmlPath, string sourceRoot, IEnumerable<SyntheticSuite.TestClass> classes, int hitFactor = 1, bool coverAll = false)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) };
        using var w = XmlWriter.Create(xmlPath, settings);
        w.WriteStartDocument(standalone: true);
        w.WriteStartElement("coverage");
        w.WriteAttributeString("line-rate", "0.5");
        w.WriteAttributeString("branch-rate", "0.5");
        w.WriteAttributeString("version", "1.9");
        w.WriteAttributeString("timestamp", "1791531265");
        w.WriteStartElement("sources");
        w.WriteElementString("source", sourceRoot.EndsWith('/') ? sourceRoot : sourceRoot + "/");
        w.WriteEndElement();
        w.WriteStartElement("packages");
        var index = 0;
        foreach (var module in classes.GroupBy(c => c.Module))
        {
            WritePackage(w, module.Key, module.Select(c => (c, index++)).ToList(), sourceRoot, hitFactor, coverAll);
        }
        // the test assembly (excluded by the caller, as Labcoat excludes test projects)
        var first = classes.FirstOrDefault();
        if (first is not null)
            WritePackage(w, first.ProjectId, [(first with { ProductFile = first.TestFile }, 0)], sourceRoot, hitFactor, coverAll);
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndDocument();
    }

    private static void WritePackage(XmlWriter w, string module, List<(SyntheticSuite.TestClass Class, int Index)> classes, string sourceRoot, int hitFactor, bool coverAll)
    {
        w.WriteStartElement("package");
        w.WriteAttributeString("name", module);
        w.WriteAttributeString("line-rate", "0.5");
        w.WriteAttributeString("branch-rate", "0.5");
        w.WriteAttributeString("complexity", "1");
        w.WriteStartElement("classes");
        foreach (var (c, index) in classes)
        {
            var relative = Path.GetRelativePath(sourceRoot, c.ProductFile).Replace('\\', '/');
            w.WriteStartElement("class");
            w.WriteAttributeString("name", "Contoso." + Path.GetFileNameWithoutExtension(c.ProductFile));
            w.WriteAttributeString("filename", relative);
            w.WriteAttributeString("line-rate", "0.5");
            w.WriteAttributeString("branch-rate", "0.5");
            w.WriteAttributeString("complexity", "1");
            var methods = SyntheticSuite.ProductFileLines / SyntheticSuite.MethodLines - 2;
            var lines = new List<(int Number, int Hits, bool Branch)>();
            w.WriteStartElement("methods");
            for (var m = 0; m < methods; m++)
            {
                var hit = coverAll || (m + index) % 2 == 0;
                w.WriteStartElement("method");
                w.WriteAttributeString("name", "Method" + m.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("signature", "(int)");
                w.WriteAttributeString("line-rate", hit ? "1" : "0");
                w.WriteAttributeString("branch-rate", "1");
                w.WriteAttributeString("complexity", "1");
                w.WriteStartElement("lines");
                var start = 10 + m * SyntheticSuite.MethodLines;
                for (var l = start; l < start + SyntheticSuite.MethodLines - 1; l++)
                {
                    var hits = hit ? 1 + (l * hitFactor) % 7 : 0;
                    var branch = l % 4 == 0;
                    WriteLine(w, l, hits, branch);
                    lines.Add((l, hits, branch));
                }
                w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteStartElement("lines");
            foreach (var (number, hits, branch) in lines) WriteLine(w, number, hits, branch);
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteLine(XmlWriter w, int number, int hits, bool branch)
    {
        w.WriteStartElement("line");
        w.WriteAttributeString("number", number.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("hits", hits.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("branch", branch ? "True" : "False");
        if (branch) w.WriteAttributeString("condition-coverage", hits > 0 ? "50% (1/2)" : "0% (0/2)");
        w.WriteEndElement();
    }
}
