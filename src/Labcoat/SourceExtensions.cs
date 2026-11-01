namespace Labcoat;

/// <summary>File extensions that tell what a changed file is.</summary>
internal static class SourceExtensions
{
    /// <summary>C#, F#, VB and Razor source: code the impact map and the smart watch map to tests.</summary>
    public static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".fs", ".vb", ".razor" };
}
