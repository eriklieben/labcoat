using System.Text.RegularExpressions;

namespace Labcoat;

/// <summary>
/// The match timeout of Labcoat's regular expressions (every <c>[GeneratedRegex]</c> and the coverage glob patterns).
/// Every input is local (project and solution files, test and build output, git output, the bundled index.html) and the
/// patterns are linear, so only pathological input can hit it: the helpers treat a timeout as "no match" instead of
/// failing the request that is being answered.
/// </summary>
internal static class RegexTimeout
{
    public const int Milliseconds = 1000;

    public static readonly TimeSpan Value = TimeSpan.FromMilliseconds(Milliseconds);

    /// <summary><see cref="Regex.Match(string)"/>; <see cref="Match.Empty"/> (no match) when it times out.</summary>
    public static Match MatchOrEmpty(this Regex regex, string input)
    {
        try { return regex.Match(input); }
        catch (RegexMatchTimeoutException) { return Match.Empty; }
    }

    /// <summary><see cref="Regex.IsMatch(string)"/>; false when it times out.</summary>
    public static bool IsMatchOrFalse(this Regex regex, string input)
    {
        try { return regex.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Every match of <see cref="Regex.Matches(string)"/>; none when it times out.</summary>
    public static List<Match> MatchesOrEmpty(this Regex regex, string input)
    {
        try { return regex.Matches(input).ToList(); }
        catch (RegexMatchTimeoutException) { return []; }
    }
}
