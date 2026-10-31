namespace Labcoat;

/// <summary>Wording shared by the log lines and the console output.</summary>
internal static class Words
{
    /// <summary>"s" unless <paramref name="count"/> is 1: <c>$"{n} test{Words.Plural(n)}"</c>.</summary>
    public static string Plural(int count) => count == 1 ? "" : "s";
}
