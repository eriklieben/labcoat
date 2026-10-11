namespace Labcoat.Testing;

/// <summary>
/// A parsed trace filter (<see cref="TraceFilterSyntax"/>): which tests get a trace. Matches the way the Labcoat UI's
/// search matches the same text, against the test's project, names and traits (<see cref="LabcoatTest"/>).
/// </summary>
internal sealed class TraceFilter
{
    /// <summary>The trait key Labcoat shows a category under (<c>Category</c>), lower case.</summary>
    private const string CategoryKey = "category";

    private readonly IReadOnlyList<TraceFilterTerm> _terms;

    private TraceFilter(string text, IReadOnlyList<TraceFilterTerm> terms, string? error)
    {
        Text = text;
        _terms = terms;
        Error = error;
    }

    /// <summary>The filter as written.</summary>
    public string Text { get; }

    /// <summary>Why the filter is not valid; null when it is.</summary>
    public string? Error { get; }

    /// <summary>Parses <paramref name="text"/>; an invalid filter has an <see cref="Error"/> and matches no test.</summary>
    public static TraceFilter Compile(string text)
    {
        try
        {
            return new TraceFilter(text, TraceFilterSyntax.Parse(text), null);
        }
        catch (FormatException ex)
        {
            return new TraceFilter(text, [], ex.Message);
        }
    }

    /// <summary>True when every term matches <paramref name="test"/> of the test project <paramref name="project"/>.</summary>
    public bool Matches(LabcoatTest test, string? project)
    {
        if (Error is not null) return false;
        var traits = TraitsOf(test);
        var search = SearchText(test, project, traits);
        return _terms.All(term => term.Kind switch
        {
            TraceFilterTermKind.Category => traits.Exists(t => t.Value.Contains(term.Value, StringComparison.Ordinal)),
            TraceFilterTermKind.Trait => traits.Exists(t => t.Key == term.Value && (term.TraitValue is null || t.Value.Contains(term.TraitValue, StringComparison.Ordinal))),
            TraceFilterTermKind.Project => Lower(project).Contains(term.Value, StringComparison.Ordinal),
            _ => search.Contains(term.Value, StringComparison.Ordinal),
        });
    }

    /// <summary>The categories (as <c>Category</c> traits) and traits of the test, lower case.</summary>
    private static List<KeyValuePair<string, string>> TraitsOf(LabcoatTest test) =>
        [
            .. test.Categories.Select(c => new KeyValuePair<string, string>(CategoryKey, Lower(c))),
            .. test.Traits.Select(t => new KeyValuePair<string, string>(Lower(t.Key), Lower(t.Value))),
        ];

    /// <summary>Lower case; a null (a default <see cref="KeyValuePair{TKey, TValue}"/>, say) as empty.</summary>
    private static string Lower(string? s) => s?.ToLowerInvariant() ?? "";

    /// <summary>
    /// What free text is found in, as the UI's search text of a test (project, <c>Namespace.Class.DisplayName</c>, trait
    /// values), plus the method name.
    /// </summary>
    private static string SearchText(LabcoatTest test, string? project, List<KeyValuePair<string, string>> traits)
    {
        var name = string.Join('.', new[] { test.ClassName, test.DisplayName }.Where(s => !string.IsNullOrEmpty(s)));
        return $"{project} {name} {test.MethodName} {string.Join(' ', traits.Select(t => t.Value))}".ToLowerInvariant();
    }
}
