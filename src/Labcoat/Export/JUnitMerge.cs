using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Labcoat;

/// <summary>The totals of a merged JUnit report.</summary>
public sealed record JUnitTotals(int Suites, int Tests, int Failures, int Errors, int Skipped, double Seconds)
{
    /// <summary>Failed or errored test cases.</summary>
    public int Failed => Failures + Errors;
    public int Passed => Math.Max(0, Tests - Failed - Skipped);
}

/// <summary>
/// Combines JUnit XML reports (the per-shard files of a split run) into one: suites of the same name are joined, a
/// test case seen twice (same suite, classname and name) keeps the last one, and the totals are recomputed from the
/// test cases. Valid against junit-10.xsd like <see cref="JUnitWriter"/>'s output when its inputs are. Inputs are read
/// with a forward-only reader; only the test case elements are kept in memory (until <see cref="Write"/>).
/// The first suite's <c>properties</c> are kept, plus <c>labcoat.mergedFrom</c>: how many reports the suite came from.
/// </summary>
public sealed class JUnitMerge
{
    /// <summary>The property that counts the reports a suite was merged from (a merged report counts as its own count).</summary>
    public const string MergedFromProperty = "labcoat.mergedFrom";

    private sealed class Suite(string name)
    {
        public string Name { get; } = name;
        public string? Package { get; set; }
        public string? Timestamp { get; set; }
        public List<(string Name, string Value)>? Properties { get; set; }
        public int MergedFrom { get; set; }
        public List<Case> Cases { get; } = [];
        public Dictionary<(string Class, string Name), int> Index { get; } = [];
    }

    private sealed record Case(XElement Element, string Source);

    private readonly List<Suite> _suites = [];
    private readonly Dictionary<string, Suite> _byName = new(StringComparer.Ordinal);
    private string? _name;

    /// <summary>Test cases replaced by a later one with the same suite, classname and name (one message each).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Reports added so far.</summary>
    public int Inputs { get; private set; }

    /// <summary>Adds a report; <paramref name="source"/> names it in warnings. Throws <see cref="XmlException"/> for invalid XML.</summary>
    public void Add(string source, Stream stream)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };
        using var reader = XmlReader.Create(stream, settings);
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName is not ("testsuites" or "testsuite"))
            throw new XmlException("the root element is not <testsuites> or <testsuite>");
        Inputs++;
        var touched = new HashSet<Suite>();
        if (reader.LocalName == "testsuites")
        {
            _name ??= reader.GetAttribute("name");
            ReadSuites(reader, source, touched);
        }
        else ReadSuite(reader, source, touched);
    }

    private void ReadSuites(XmlReader r, string source, HashSet<Suite> touched)
    {
        if (r.IsEmptyElement) return;
        var depth = r.Depth;
        r.Read();
        while (r.Depth > depth)
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "testsuite") ReadSuite(r, source, touched);
            else r.Skip();
        }
    }

    /// <summary>Reads one suite (and the suites nested in it, which become suites of their own); leaves the reader after its end tag.</summary>
    private void ReadSuite(XmlReader r, string source, HashSet<Suite> touched)
    {
        var name = r.GetAttribute("name") ?? "";
        if (!_byName.TryGetValue(name, out var suite))
        {
            suite = new Suite(name);
            _byName[name] = suite;
            _suites.Add(suite);
        }
        suite.Package ??= r.GetAttribute("package");
        if (r.GetAttribute("timestamp") is { Length: > 0 } stamp && (suite.Timestamp is null || string.CompareOrdinal(stamp, suite.Timestamp) < 0)) suite.Timestamp = stamp;
        var mergedFrom = 1;
        if (r.IsEmptyElement)
        {
            r.Read();
        }
        else
        {
            var depth = r.Depth;
            r.Read();
            while (r.Depth > depth)
            {
                if (r.NodeType != XmlNodeType.Element) { r.Skip(); continue; }
                switch (r.LocalName)
                {
                    case "testcase":
                        AddCase(suite, (XElement)XNode.ReadFrom(r), source);
                        break;
                    case "properties":
                        var properties = ReadProperties((XElement)XNode.ReadFrom(r));
                        if (properties.Find(p => p.Name == MergedFromProperty) is { } m && int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0) mergedFrom = n;
                        suite.Properties ??= properties.Where(p => p.Name != MergedFromProperty).ToList();
                        break;
                    case "testsuite":
                        ReadSuite(r, source, touched);
                        break;
                    default:
                        r.Skip();
                        break;
                }
            }
            r.Read(); // the end tag
        }
        if (touched.Add(suite)) suite.MergedFrom += mergedFrom;
    }

    private static List<(string Name, string Value)> ReadProperties(XElement properties) =>
        properties.Elements("property").Select(p => ((string?)p.Attribute("name") ?? "", (string?)p.Attribute("value") ?? p.Value)).ToList();

    private void AddCase(Suite suite, XElement element, string source)
    {
        var classname = (string?)element.Attribute("classname");
        if (string.IsNullOrEmpty(classname))
        {
            classname = suite.Name; // as JUnitWriter: a test without a class is filed under its project
            element.SetAttributeValue("classname", classname);
        }
        var key = (classname, (string?)element.Attribute("name") ?? "");
        if (suite.Index.TryGetValue(key, out var at))
        {
            Warnings.Add($"Test '{classname}.{key.Item2}' of suite '{suite.Name}' is in {suite.Cases[at].Source} and {source}: keeping the one of {source}.");
            suite.Cases[at] = new Case(element, source);
            return;
        }
        suite.Index[key] = suite.Cases.Count;
        suite.Cases.Add(new Case(element, source));
    }

    public JUnitTotals Totals()
    {
        var all = _suites.SelectMany(s => s.Cases).Select(c => c.Element).ToList();
        return new JUnitTotals(_suites.Count, all.Count, Count(all, "failure"), Count(all, "error"), Count(all, "skipped"), all.Sum(Seconds));
    }

    private static int Count(IEnumerable<XElement> cases, string child) => cases.Count(c => c.Element(child) is not null);

    private static double Seconds(XElement testcase) =>
        double.TryParse((string?)testcase.Attribute("time"), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 0;

    private static readonly XmlWriterSettings Settings = new()
    {
        Indent = true,
        Encoding = new UTF8Encoding(false),
        NewLineChars = "\n",
        CheckCharacters = true,
    };

    /// <summary>Writes the merged report as UTF-8 to <paramref name="stream"/> (left open).</summary>
    public void Write(Stream stream)
    {
        using var w = XmlWriter.Create(stream, Settings);
        var totals = Totals();
        w.WriteStartDocument();
        w.WriteStartElement("testsuites");
        w.WriteAttributeString("name", ExportText.Clean(_name is { Length: > 0 } ? _name : "Labcoat merged"));
        w.WriteAttributeString("tests", Int(totals.Tests));
        w.WriteAttributeString("failures", Int(totals.Failures));
        w.WriteAttributeString("errors", Int(totals.Errors));
        w.WriteAttributeString("time", JUnitWriter.Seconds(totals.Seconds * 1000));
        var index = 0;
        foreach (var suite in _suites) WriteSuite(w, suite, index++);
        w.WriteEndElement();
        w.WriteEndDocument();
    }

    private static void WriteSuite(XmlWriter w, Suite suite, int index)
    {
        var cases = suite.Cases.Select(c => c.Element).ToList();
        w.WriteStartElement("testsuite");
        w.WriteAttributeString("name", ExportText.Clean(suite.Name));
        w.WriteAttributeString("tests", Int(cases.Count));
        w.WriteAttributeString("failures", Int(Count(cases, "failure")));
        w.WriteAttributeString("errors", Int(Count(cases, "error")));
        w.WriteAttributeString("skipped", Int(Count(cases, "skipped")));
        w.WriteAttributeString("time", JUnitWriter.Seconds(cases.Sum(Seconds) * 1000));
        if (suite.Timestamp is not null) w.WriteAttributeString("timestamp", suite.Timestamp);
        w.WriteAttributeString("id", Int(index));
        if (suite.Package is not null) w.WriteAttributeString("package", ExportText.Clean(suite.Package));

        w.WriteStartElement("properties");
        foreach (var (name, value) in (suite.Properties ?? []).Append((MergedFromProperty, Int(suite.MergedFrom))))
        {
            w.WriteStartElement("property");
            w.WriteAttributeString("name", name);
            w.WriteAttributeString("value", ExportText.Clean(value));
            w.WriteEndElement();
        }
        w.WriteEndElement();

        foreach (var c in cases) c.WriteTo(w);
        w.WriteEndElement();
    }

    private static string Int(int n) => n.ToString(CultureInfo.InvariantCulture);
}
