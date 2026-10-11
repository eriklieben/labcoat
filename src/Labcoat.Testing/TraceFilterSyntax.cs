// Shared source: compiled into Labcoat.Testing (which matches tests against the filter), and linked into Labcoat and
// Labcoat.Aspire.Hosting, which check a filter when they start (an invalid one is an error there, not in every test).
namespace Labcoat.Testing;

/// <summary>What a term of a trace filter matches (see <see cref="TraceFilterSyntax"/>).</summary>
internal enum TraceFilterTermKind
{
    /// <summary>Free text: the project, namespace, class, method or display name, or a trait value.</summary>
    Text,
    /// <summary><c>cat:</c> a trait value (a category, or the value of any trait).</summary>
    Category,
    /// <summary><c>trait:key</c> or <c>trait:key=value</c>.</summary>
    Trait,
    /// <summary><c>proj:</c> the test project.</summary>
    Project,
}

/// <summary>
/// One term of a trace filter. <see cref="Value"/> is lower case; for <see cref="TraceFilterTermKind.Trait"/> it is the
/// key and <see cref="TraitValue"/> the value (null: any value).
/// </summary>
internal sealed record TraceFilterTerm(TraceFilterTermKind Kind, string Value, string? TraitValue = null);

/// <summary>
/// The syntax of a trace filter: the subset of the Labcoat UI's search (ui/src/tree.ts, <c>parseQuery</c>) that is
/// known before a test runs. Words separated by white space, <c>"quoted values"</c> (also after a prefix:
/// <c>cat:"slow tests"</c>); every term must match (AND), case-insensitive, by substring:
/// <list type="bullet">
/// <item>free text: the project, namespace, class, method or display name, or a trait value;</item>
/// <item><c>cat:&lt;text&gt;</c>: a trait value, e.g. a category (<c>[TestCategory]</c>, NUnit / TUnit <c>[Category]</c>,
/// an xUnit <c>Category</c> trait);</item>
/// <item><c>trait:&lt;key&gt;</c> / <c>trait:&lt;key&gt;=&lt;text&gt;</c>: a trait with exactly that key (and a value
/// containing the text); categories are traits with the key <c>Category</c>;</item>
/// <item><c>proj:&lt;text&gt;</c>: the test project (its assembly name).</item>
/// </list>
/// <c>is:</c> and <c>msg:</c> depend on a test's result, so they are not allowed; nor is a prefix without a value or an
/// unclosed quote. Other prefixes are free text, as in the UI.
/// </summary>
internal static class TraceFilterSyntax
{
    /// <summary>A short description of the syntax, for error messages.</summary>
    public const string Expected = "words to find in the test's name, class or namespace, cat:<category>, trait:<key>[=<value>] or proj:<project>";

    private const char Quote = '"';
    private const string CategoryPrefix = "cat";
    private const string TraitPrefix = "trait";
    private const string ProjectPrefix = "proj";

    /// <summary>The terms of <paramref name="text"/>; empty for a blank filter (every test matches).</summary>
    /// <exception cref="FormatException">The filter is not valid; the message says why.</exception>
    public static IReadOnlyList<TraceFilterTerm> Parse(string? text)
    {
        var terms = new List<TraceFilterTerm>();
        foreach (var word in Words(text ?? ""))
            terms.Add(Term(word));
        return terms;
    }

    /// <summary>Null when <paramref name="text"/> is a valid filter (or blank), else why it is not.</summary>
    public static string? Validate(string? text)
    {
        try
        {
            Parse(text);
            return null;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    private static TraceFilterTerm Term(string word)
    {
        var colon = word.IndexOf(':');
        var prefix = colon > 0 ? word[..colon].ToLowerInvariant() : "";
        var value = colon > 0 ? word[(colon + 1)..].ToLowerInvariant() : "";
        switch (prefix)
        {
            case "is" or "msg":
                throw new FormatException($"'{prefix}:' depends on the test's result, which is not known when the test starts; use {Expected}.");
            case CategoryPrefix or TraitPrefix or ProjectPrefix when value.Length == 0:
                throw new FormatException($"'{prefix}:' needs a value, e.g. {Example(prefix)}.");
            case CategoryPrefix:
                return new TraceFilterTerm(TraceFilterTermKind.Category, value);
            case TraitPrefix:
                return TraitTerm(value);
            case ProjectPrefix:
                return new TraceFilterTerm(TraceFilterTermKind.Project, value);
            default:
                return new TraceFilterTerm(TraceFilterTermKind.Text, word.ToLowerInvariant());
        }
    }

    /// <summary><c>key</c> or <c>key=value</c>; like the UI (<c>split("=", 2)</c>) the value ends at a second <c>=</c>.</summary>
    private static TraceFilterTerm TraitTerm(string value)
    {
        var parts = value.Split('=');
        if (parts[0].Length == 0) throw new FormatException($"'{TraitPrefix}:' needs a key, e.g. {Example(TraitPrefix)}.");
        return new TraceFilterTerm(TraceFilterTermKind.Trait, parts[0], parts.Length > 1 ? parts[1] : null);
    }

    private static string Example(string prefix) => prefix switch
    {
        CategoryPrefix => "cat:Integration",
        TraitPrefix => "trait:Kind=Slow",
        _ => "proj:Orders.Tests",
    };

    /// <summary>The words of a filter: split on white space, keeping <c>"quoted values"</c> (as the UI's <c>words</c>).</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            var j = i;
            while (j < text.Length && text[j] != Quote && !char.IsWhiteSpace(text[j])) j++;
            if (j < text.Length && text[j] == Quote)
            {
                // A quoted part: up to the closing quote; the text before the quote is a prefix (cat:"a b").
                var close = text.IndexOf(Quote, j + 1);
                if (close < 0) throw new FormatException($"The quote at position {j + 1} is not closed.");
                words.Add(string.Concat(text.AsSpan(i, j - i), text.AsSpan(j + 1, close - j - 1)));
                i = close + 1;
            }
            else
            {
                words.Add(text[i..j]);
                i = j;
            }
        }
        words.RemoveAll(w => w.Length == 0);
        return words;
    }
}
